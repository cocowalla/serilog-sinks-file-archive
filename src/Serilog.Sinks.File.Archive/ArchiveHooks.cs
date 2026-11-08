using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Debugging;

namespace Serilog.Sinks.File.Archive
{
    /// <summary>
    /// Archives log files before they are deleted by Serilog's retention mechanism, copying them to another location
    /// and optionally compressing them using GZip.
    /// </summary>
    /// <remarks>
    /// Archiving is synchronous unless explicitly enabled through an <c>archiveAsync</c> constructor argument.
    /// If background compression is aborted, the uncompressed source remains at its renamed
    /// <c>{originalPath}.{guid}.archive-pending</c> path for manual recovery.
    /// </remarks>
    public class ArchiveHooks : FileLifecycleHooks
    {
        private const string CompressionPendingExtension = ".archive-pending";

        private readonly bool archiveAsync;
        private readonly CompressionLevel compressionLevel;
        private readonly int retainedFileCountLimit;
        private readonly string targetDirectory;

        // Protects destination archive writes and retention across hook instances so another job cannot overwrite
        // or delete an archive while it is being written. Renamed sources belong to individual jobs; renaming
        // or deleting them does not need ArchiveLock. Pending source filenames are excluded from retention.
        private static readonly object ArchiveLock = new object();

        // Protects updates and snapshots of pendingArchivesTaskQueue so concurrent callers cannot lose queued jobs.
        // Renaming, compression, copying and waiting for jobs happen outside this lock.
        private readonly object pendingArchivesTaskQueueLock = new object();
        private Task pendingArchivesTaskQueue = Task.CompletedTask;

        /// <summary>
        /// Creates hooks that synchronously archive obsolete log files before Serilog deletes them.
        /// </summary>
        /// <param name="compressionLevel">
        /// GZip compression level, or <see cref="CompressionLevel.NoCompression"/> to copy files.
        /// </param>
        /// <param name="targetDirectory">
        /// Archive directory, optionally containing date tokens. Null keeps compressed archives in the source directory.
        /// </param>
        /// <exception cref="ArgumentException">Compression is disabled and no target directory is provided.</exception>
        public ArchiveHooks(CompressionLevel compressionLevel = CompressionLevel.Fastest, string targetDirectory = null)
            : this(false, compressionLevel, targetDirectory)
        {
        }

        /// <summary>
        /// Creates hooks that optionally archive obsolete log files in the background.
        /// </summary>
        /// <param name="archiveAsync">
        /// If true, synchronously renames each source file within its directory, then queues archiving on the thread pool.
        /// This means serilog, or following OnFileDelete hooks will not find this file at the expected location.
        /// If false, completes archiving before returning from <see cref="OnFileDeleting"/>.
        /// </param>
        /// <param name="compressionLevel">GZip compression level, or <see cref="CompressionLevel.NoCompression"/> to copy files.</param>
        /// <param name="targetDirectory">
        /// Archive directory, optionally containing date tokens. Null keeps compressed archives in the source directory.
        /// Tokens are expanded when the file is queued, rather than when background work runs.
        /// </param>
        /// <exception cref="ArgumentException">Compression is disabled and no target directory is provided.</exception>
        /// <remarks>
        /// Background jobs execute one at a time per instance. Failures are reported through <see cref="SelfLog"/>;
        /// the renamed source is retained as <c>{originalPath}.{guid}.archive-pending</c> for manual recovery.
        /// Aborted compression leaves the uncompressed source at its renamed path, including when the application exits.
        /// Use <see cref="BackgroundRunningArchiveTask"/> to optionally wait for queued work.
        /// Pending files are not automatically retried after failure or process termination.
        /// </remarks>
        public ArchiveHooks(bool archiveAsync, CompressionLevel compressionLevel = CompressionLevel.Fastest, string targetDirectory = null)
        {
            if (compressionLevel == CompressionLevel.NoCompression && targetDirectory == null)
            {
                throw new ArgumentException($"Either {nameof(compressionLevel)} or {nameof(targetDirectory)} must be set");
            }

            this.archiveAsync = archiveAsync;
            this.compressionLevel = compressionLevel;
            this.targetDirectory = targetDirectory;
        }

        /// <summary>
        /// Creates hooks that synchronously archive obsolete log files and limit the number of archives retained.
        /// </summary>
        /// <param name="retainedFileCountLimit">Maximum number of archives to retain for each log filename prefix; must be greater than zero.</param>
        /// <param name="compressionLevel">
        /// GZip compression level, or <see cref="CompressionLevel.NoCompression"/> to copy files.
        /// </param>
        /// <param name="targetDirectory">
        /// Constant archive directory. Null keeps compressed archives in the source directory. Date tokens are not supported.
        /// </param>
        /// <exception cref="ArgumentException">
        /// The count limit is not positive, the target directory contains tokens, or compression is disabled without a target directory.
        /// </exception>
        /// <remarks>
        /// Cleanup orders archives by filename length first, then by an ordinal, case-insensitive filename comparison;
        /// file creation and modification times are not used. This supports increasing numeric rolling suffixes, but
        /// chronological retention requires consistent naming and date formats that sort chronologically.
        /// Non-ISO dates or mixed filename/suffix widths can cause newer archives to be deleted before older ones.
        /// </remarks>
        public ArchiveHooks(int retainedFileCountLimit, CompressionLevel compressionLevel = CompressionLevel.Fastest, string targetDirectory = null)
            : this(retainedFileCountLimit, false, compressionLevel, targetDirectory)
        {
        }

        /// <summary>
        /// Creates hooks with an archive count limit and optional background archiving.
        /// </summary>
        /// <param name="retainedFileCountLimit">Maximum number of archives to retain for each log filename prefix; must be greater than zero.</param>
        /// <param name="archiveAsync">
        /// If true, renames source files synchronously and queues archiving on the thread pool; otherwise archives synchronously.
        /// </param>
        /// <param name="compressionLevel">GZip compression level, or <see cref="CompressionLevel.NoCompression"/> to copy files.</param>
        /// <param name="targetDirectory">
        /// Constant archive directory. Null keeps compressed archives in the source directory.
        /// Date tokens are not supported when an archive count limit is specified.
        /// </param>
        /// <exception cref="ArgumentException">
        /// The count limit is not positive, the target directory contains tokens, or compression is disabled without a target directory.
        /// </exception>
        /// <remarks>
        /// Retention is applied after each successful archive. Background failures are reported through <see cref="SelfLog"/>
        /// and leave the renamed source for manual recovery. Aborted compression also leaves the uncompressed source
        /// at its renamed path. Use <see cref="BackgroundRunningArchiveTask"/> to optionally wait for queued work.
        /// Cleanup orders archives by filename length first, then by an ordinal, case-insensitive filename comparison;
        /// file creation and modification times are not used. Non-ISO dates or mixed filename/suffix widths can cause
        /// newer archives to be deleted before older ones, even when date components use ISO 8601 formatting.
        /// </remarks>
        public ArchiveHooks(int retainedFileCountLimit, bool archiveAsync, CompressionLevel compressionLevel = CompressionLevel.Fastest, string targetDirectory = null)
            : this(archiveAsync, compressionLevel, targetDirectory)
        {
            if (retainedFileCountLimit <= 0)
            {
                throw new ArgumentException($"{nameof(retainedFileCountLimit)} must be greater than zero", nameof(retainedFileCountLimit));
            }

            if (targetDirectory is not null && TokenExpander.IsTokenised(targetDirectory))
            {
                throw new ArgumentException($"{nameof(targetDirectory)} must not be tokenised when using {nameof(retainedFileCountLimit)}", nameof(targetDirectory));
            }

            this.retainedFileCountLimit = retainedFileCountLimit;
        }

        /// <summary>
        /// Archives a file before Serilog deletes it, or renames it and queues background archiving when enabled.
        /// </summary>
        /// <param name="path">Path of the obsolete log file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <remarks>
        /// Rename failures and synchronous archive failures are logged to <see cref="SelfLog"/> and rethrown,
        /// allowing Serilog to skip deletion. Background failures are logged and retain the renamed source.
        /// In background mode, subsequent lifecycle hooks cannot access the original path after this method returns.
        /// </remarks>
        public override void OnFileDeleting(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            try
            {
                path = Path.GetFullPath(path);
                var currentTargetDir = this.targetDirectory != null
                    ? Path.GetFullPath(TokenExpander.Expand(this.targetDirectory))
                    : Path.GetDirectoryName(path);

                if (!this.archiveAsync)
                {
                    ArchiveFile(path, path, currentTargetDir);
                }
                else
                {
                    // Keep the rename on the source filesystem, even when the archive is on another volume.
                    // The suffix also keeps this file outside Serilog's rolling filename pattern.
                    var pendingPath = path + "." + Guid.NewGuid().ToString("n") + CompressionPendingExtension;
                    System.IO.File.Move(path, pendingPath);

                    lock (this.pendingArchivesTaskQueueLock)
                    {
                        // A continuation chain preserves queue order without occupying a thread while idle.
                        this.pendingArchivesTaskQueue = this.pendingArchivesTaskQueue.ContinueWith(
                            _ => ArchivePendingFile(pendingPath, path, currentTargetDir),
                            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    }
                }

            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Error while archiving file {0}: {1}", path, ex);
                throw;
            }
        }

        /// <summary>
        /// Returns a task that completes when all background archives queued before this call have finished.
        /// </summary>
        /// <returns>A task representing the current queue, or a completed task if no work is pending.</returns>
        /// <remarks>
        /// Later calls to <see cref="OnFileDeleting"/> may queue additional work. This method does not stop the queue.
        /// Archive failures are reported to <see cref="SelfLog"/> and do not
        /// fault the returned task. A completed task therefore does not guarantee that every archive succeeded.
        /// </remarks>
        public Task BackgroundRunningArchiveTask()
        {
            lock (this.pendingArchivesTaskQueueLock)
            {
                return this.pendingArchivesTaskQueue;
            }
        }

        private void ArchivePendingFile(string pendingPath, string originalPath, string currentTargetDir)
        {
            try
            {
                ArchiveFile(pendingPath, originalPath, currentTargetDir);
                // Delete the uncompressed source only after archiving succeeds; aborted compression leaves it intact.
                System.IO.File.Delete(pendingPath);
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Error while archiving file {0}; source retained at {1}: {2}", originalPath, pendingPath, ex);
            }
        }

        private void ArchiveFile(string sourcePath, string originalPath, string currentTargetDir)
        {
            var filename = Path.GetFileName(originalPath);
            if (this.compressionLevel != CompressionLevel.NoCompression)
            {
                filename += ".gz";
            }

            // Directory creation is idempotent and does not need to block other archive writers.
            Directory.CreateDirectory(currentTargetDir);
            var targetPath = Path.Combine(currentTargetDir, filename);
            List<KeyValuePair<string, Exception>> deletionErrors = [];

            // Writing and retention share one critical section because the final archive is visible while being written.
            lock (ArchiveLock)
            {
                if (this.compressionLevel == CompressionLevel.NoCompression)
                {
                    System.IO.File.Copy(sourcePath, targetPath, true);
                }
                else
                {
                    using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var compressStream = new GZipStream(targetStream, this.compressionLevel))
                    {
                        sourceStream.CopyTo(compressStream);
                    }
                }

                if (this.retainedFileCountLimit > 0)
                {
                    deletionErrors = RemoveExcessFiles(currentTargetDir, originalPath);
                }
            }

            // SelfLog can invoke application code; do not hold the archive lock while reporting errors.
            foreach (var error in deletionErrors)
            {
                SelfLog.WriteLine("Error while deleting file {0}: {1}", error.Key, error.Value);
            }
        }

        /// <summary>
        /// Clean up excess files
        /// </summary>
        /// <param name="folder"></param>
        /// <param name="filePath"></param>
        /// <returns>A list with all errors that occurred while deleting files, the key of the list is the file path. Return an empty list if no errors occurred.</returns>
        private List<KeyValuePair<string, Exception>> RemoveExcessFiles(string folder, string filePath)
        {
            // The caller holds ArchiveLock throughout writing and retention of destination archives.
            // Return deletion failures so the caller can report them after releasing the lock.
            List<KeyValuePair<string, Exception>> errors = new();
            var baseFileName = Path.GetFileNameWithoutExtension(filePath).Split('_').First();
            var searchPattern = this.compressionLevel != CompressionLevel.NoCompression
                ? "*.gz"
                : "*.*";

            var filesToDelete = Directory.GetFiles(folder, searchPattern)
                .Where(f => !f.EndsWith(CompressionPendingExtension, StringComparison.OrdinalIgnoreCase))
                .Where(f => Path.GetFileNameWithoutExtension(f).StartsWith(baseFileName))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f, LogFileComparer.Default)
                .Skip(this.retainedFileCountLimit)
                .ToList();

            foreach (var file in filesToDelete)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    errors.Add(new KeyValuePair<string, Exception>(file.FullName, ex));
                }
            }

            return errors;
        }

        /// <summary>
        /// Orders archive filenames by length first, then by an ordinal, case-insensitive comparison.
        /// </summary>
        /// <remarks>
        /// Cleanup reverses this ordering to retain the longest, then lexicographically greatest names.
        /// Length-first ordering handles numeric rolling suffixes growing beyond their padding width.
        /// This comparer does not inspect timestamps or parse dates or sequence numbers: non-ISO dates,
        /// variable-width components, and mixed date/suffix widths need not sort chronologically.
        /// </remarks>
        private class LogFileComparer : IComparer<FileInfo>
        {
            public static readonly IComparer<FileInfo> Default = new LogFileComparer();

            public int Compare(FileInfo x, FileInfo y)
            {
                if (x is null && y is null)
                    return 0;
                if (x is null)
                    return -1;
                if (y is null)
                    return 1;
                if (x.Name.Length > y.Name.Length)
                    return 1;
                if (y.Name.Length > x.Name.Length)
                    return -1;

                return String.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
