using System;
using System.IO;
using System.IO.Compression;
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
                throw new ArgumentException("Either compressionLevel or targetDirectory must be set");

            this.compressionLevel = compressionLevel;
            this.targetDirectory = targetDirectory;
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
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Error while archiving file {0}: {1}", path, ex);
                throw;
            }
        }
    }
}
