using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Shouldly;
using Xunit;
using Serilog.Events;
using Serilog.Sinks.File.Archive.Tests.Support;

namespace Serilog.Sinks.File.Archive.Tests
{
    public class RollingFileSinkTests
    {
        private static readonly LogEvent[] LogEvents = {
            Some.LogEvent(),
            Some.LogEvent(),
            Some.LogEvent()
        };

        // Test for removing old archive files in the same directory
        [Fact]
        public void Should_remove_old_archives()
        {
            var retainedFiles = 1;
            var archiveWrapper = new ArchiveHooks(retainedFiles);

            using (var temp = TempFolder.ForCaller())
            {
                var path = temp.AllocateFilename("log");

                // Write events, such that we end up with 2 deleted files and 1 retained file
                WriteLogEvents(path, archiveWrapper, LogEvents);

                // Get all the files in the test directory
                var files = Directory.GetFiles(temp.Path)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // We should have a single log file, and 'retainedFiles' gz files
                files.Count(x => x.EndsWith("log")).ShouldBe(1);
                files.Count(x => x.EndsWith("gz")).ShouldBe(retainedFiles);

                // Ensure the data was GZip compressed, by decompressing and comparing against what we wrote
                int i = LogEvents.Length - retainedFiles - 1;
                foreach (var gzipFile in files.Where(x => x.EndsWith("gz")))
                {
                    var lines = Utils.DecompressLines(gzipFile);

                    lines.Count.ShouldBe(1);
                    lines[0].ShouldEndWith(LogEvents[i].MessageTemplate.Text);
                    i++;
                }
            }
        }

        // Test for compressing log files in the same directory
        [Fact]
        public void Should_compress_deleting_log_files_in_place()
        {
            var archiveWrapper = new ArchiveHooks();

            using (var temp = TempFolder.ForCaller())
            {
                var path = temp.AllocateFilename("log");

                // Write events, such that we end up with 2 deleted files and 1 retained file
                WriteLogEvents(path, archiveWrapper, LogEvents);

                // Get all the files in the test directory
                var files = Directory.GetFiles(temp.Path)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // We should have a single log file, and 2 gz files
                files.Count(x => x.EndsWith("log")).ShouldBe(1);
                files.Count(x => x.EndsWith("gz")).ShouldBe(2);

                // Ensure the data was GZip compressed, by decompressing and comparing against what we wrote
                int i = 0;
                foreach (var gzipFile in files.Where(x => x.EndsWith("gz")))
                {
                    var lines = Utils.DecompressLines(gzipFile);

                    lines.Count.ShouldBe(1);
                    lines[0].ShouldEndWith(LogEvents[i].MessageTemplate.Text);
                    i++;
                }
            }
        }

        // Test for copying log files to another directory
        [Fact]
        public void Should_copy_deleting_log_files()
        {
            using (var temp = TempFolder.ForCaller())
            {
                var targetDirPath = temp.AllocateFolderName();
                var path = temp.AllocateFilename("log");

                var archiveWrapper = new ArchiveHooks(CompressionLevel.NoCompression, targetDirPath);

                // Write events, such that we end up with 2 deleted files and 1 retained file
                WriteLogEvents(path, archiveWrapper, LogEvents);

                // Get all the files in the test directory
                var files = Directory.GetFiles(temp.Path)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // We should only have a single log file
                files.Length.ShouldBe(1);

                // Get all the files in the target directory
                var targetFiles = Directory.GetFiles(targetDirPath)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // We should have 2 log files in the target directory
                targetFiles.Length.ShouldBe(2);
                targetFiles.ShouldAllBe(x => x.EndsWith(".log"));

                // Ensure the content matches what we wrote
                for (var i = 0; i < targetFiles.Length; i++)
                {
                    var lines = System.IO.File.ReadAllLines(targetFiles[i]);

                    lines.Length.ShouldBe(1);
                    lines[0].ShouldEndWith(LogEvents[i].MessageTemplate.Text);
                }
            }
        }

        // Test for writing compressed log files to another directory
        [Fact]
        public void Should_compress_deleting_log_files_in_target_dir()
        {
            using (var temp = TempFolder.ForCaller())
            {
                var targetDirPath = temp.AllocateFolderName();
                var path = temp.AllocateFilename("log");

                var archiveWrapper = new ArchiveHooks(CompressionLevel.Fastest, targetDirPath);

                // Write events, such that we end up with 2 deleted files and 1 retained file
                WriteLogEvents(path, archiveWrapper, LogEvents);

                // Get all the files in the test directory
                var files = Directory.GetFiles(temp.Path)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // We should only have a single log file
                files.Length.ShouldBe(1);

                // Get all the files in the target directory
                var targetFiles = Directory.GetFiles(targetDirPath)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // We should have 2 gz files in the target directory
                targetFiles.Length.ShouldBe(2);
                targetFiles.ShouldAllBe(x => x.EndsWith(".gz"));

                // Ensure the data was GZip compressed, by decompressing and comparing against what we wrote
                int i = 0;
                foreach (var gzipFile in targetFiles)
                {
                    var lines = Utils.DecompressLines(gzipFile);

                    lines.Count.ShouldBe(1);
                    lines[0].ShouldEndWith(LogEvents[i].MessageTemplate.Text);
                    i++;
                }
            }
        }

        [Fact]
        public void Should_expand_tokens_in_target_path()
        {
            using (var temp = TempFolder.ForCaller())
            {
                var subfolder = temp.AllocateFolderName();
                var targetDirPath = Path.Combine(subfolder, "{Date:yyyy}");
                var path = temp.AllocateFilename("log");

                var archiveWrapper = new ArchiveHooks(CompressionLevel.NoCompression, targetDirPath);

                // Write events, such that we end up with 2 deleted files and 1 retained file
                WriteLogEvents(path, archiveWrapper, LogEvents);

                // Get the final, expanded target directory
                var targetDirs = Directory.GetDirectories(subfolder);
                targetDirs.Length.ShouldBe(1);

                // Ensure the directory name contains the expanded date token
                targetDirs[0].ShouldEndWith(DateTime.Now.ToString("yyyy"));
            }
        }

        /// <summary>
        /// Write log events to Serilog, using a rolling log file configuration with a 1-byte size limit, so we roll
        /// after each log event, and with a retained count of 1 so 2 files will be deleted and 1 retained
        /// </summary>
        private static void WriteLogEvents(string path, ArchiveHooks hooks, LogEvent[] logEvents)
        {
            using (var log = new LoggerConfiguration()
                .WriteTo.File(path, rollOnFileSizeLimit: true, fileSizeLimitBytes: 1, retainedFileCountLimit: 1, hooks: hooks)
                .CreateLogger())
            {
                foreach (var logEvent in logEvents)
                {
                    log.Write(logEvent);
                }                
            }
        }
    }
}
