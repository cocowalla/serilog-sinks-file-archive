using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Serilog.Debugging;

namespace Serilog.Sinks.File.Archive
{
    /// <inheritdoc />
    /// <summary>
    /// Archives log files before they are deleted by Serilog's retention mechanism, copying them to another location
    /// and optionally compressing them using GZip
    /// </summary>
    public class ArchiveHooks : FileLifecycleHooks
    {
        private readonly CompressionLevel compressionLevel;
        private readonly int retainedFileCountLimit;
        private readonly string targetDirectory;

        /// <summary>
        /// Create a new ArchiveHooks, which will archive completed log files before they are deleted by Serilog's retention mechanism
        /// </summary>
        /// <param name="compressionLevel">
        /// Level of GZIP compression to use. Use CompressionLevel.NoCompression if no compression is required
        /// </param>
        /// <param name="targetDirectory">
        /// Directory in which to archive files to. Use null if compressed, archived files should remain in the same folder
        /// </param>
        public ArchiveHooks(CompressionLevel compressionLevel = CompressionLevel.Fastest, string targetDirectory = null)
        {
            if (compressionLevel == CompressionLevel.NoCompression && targetDirectory == null)
                throw new ArgumentException($"Either {nameof(compressionLevel)} or {nameof(targetDirectory)} must be set");

            this.compressionLevel = compressionLevel;
            this.targetDirectory = targetDirectory;
        }

        public ArchiveHooks(int retainedFileCountLimit, CompressionLevel compressionLevel = CompressionLevel.Fastest)
        {
            if (retainedFileCountLimit <= 0)
                throw new ArgumentException($"{nameof(retainedFileCountLimit)} must be greater than zero", nameof(retainedFileCountLimit));
            if (compressionLevel == CompressionLevel.NoCompression)
                throw new ArgumentException($"{nameof(compressionLevel)} must not be 'NoCompression' when using {nameof(retainedFileCountLimit)}", nameof(compressionLevel));

            this.compressionLevel = compressionLevel;
            this.retainedFileCountLimit = retainedFileCountLimit;
            this.targetDirectory = null;
        }

        public override void OnFileDeleting(string path)
        {
            try
            {
                // Use .gz file extension if we are going to compress the source file
                var filename = this.compressionLevel != CompressionLevel.NoCompression
                    ? Path.GetFileName(path) + ".gz"
                    : Path.GetFileName(path);

                // Determine the target path for the current file
                var currentTargetDir = this.targetDirectory != null
                    ? TokenExpander.Expand(this.targetDirectory)
                    : Path.GetDirectoryName(path);

                // Create the target directory, if it doesn't already exist
                if (!Directory.Exists(currentTargetDir))
                {
                    Directory.CreateDirectory(currentTargetDir!);
                }

                // Target file path
                var targetPath = Path.Combine(currentTargetDir, filename);

                // Do we need to compress the file, or simply copy it as-is?
                if (this.compressionLevel == CompressionLevel.NoCompression)
                {
                    System.IO.File.Copy(path, targetPath, true);
                }
                else
                {
                    using (var sourceStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var targetStream = new FileStream(targetPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                    using (var compressStream = new GZipStream(targetStream, this.compressionLevel))
                    {
                        sourceStream.CopyTo(compressStream);
                    }
                }
                //only apply archive file limit if we are archiving to a non tokenised path (constant path)
                if (this.retainedFileCountLimit > 0 && !this.IsArchivePathTokenised)
                {
                    RemoveExcessFiles(currentTargetDir);
                }
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Error while archiving file {0}: {1}", path, ex);
                throw;
            }
        }

        private bool IsArchivePathTokenised => this.targetDirectory is not null && TokenExpander.Expand(this.targetDirectory) != this.targetDirectory;

        private void RemoveExcessFiles(string folder)
        {
            var searchPattern = this.compressionLevel != CompressionLevel.NoCompression
                    ? "*.gz"
                    : "*.*";
            var filesToDelete = Directory.GetFiles(folder, searchPattern).Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).Skip(this.retainedFileCountLimit).ToList();
            foreach (var file in filesToDelete)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    SelfLog.WriteLine("Error while deleting file {0}: {1}", file.FullName, ex);
                }
            }
        }
    }
}
