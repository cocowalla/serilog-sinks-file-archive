using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Debugging;
using Serilog.Sinks.File.Archive.Tests.Support;
using Shouldly;
using Xunit;

namespace Serilog.Sinks.File.Archive.Tests
{
    // SelfLog is process-wide, so tests that replace its callback must not overlap other tests.
    [CollectionDefinition("SelfLog", DisableParallelization = true)]
    public class SelfLogCollection
    {
    }

    [Collection("SelfLog")]
    public class ArchiveHooksTests
    {
        [Fact]
        public async Task Should_rename_before_returning_and_survive_serilog_deletion()
        {
            using (var temp = new TempFolder())
            {
                var hooks = new ArchiveHooks(archiveAsync: true);
                var path = temp.AllocateFilename("log");
                System.IO.File.WriteAllText(path, "important log event");

                hooks.OnFileDeleting(path);

                System.IO.File.Exists(path).ShouldBeFalse();
                // This is what Serilog does immediately after calling the hook.
                System.IO.File.Delete(path);
                await AwaitWithTimeout(hooks.BackgroundRunningArchiveTask());

                Utils.DecompressLines(path + ".gz").ShouldBe(new[] { "important log event" });
                Directory.GetFiles(temp.Path).ShouldBe(new[] { path + ".gz" });
            }
        }

        [Fact]
        public async Task Should_queue_without_waiting_for_worker_and_continue_after_failure()
        {
            using (var temp = new TempFolder())
            using (var failureObserved = new ManualResetEventSlim())
            using (var releaseWorker = new ManualResetEventSlim())
            {
                var hooks = new ArchiveHooks(archiveAsync: true, targetDirectory: Path.Combine(temp.Path, "archive"));
                var target = Path.Combine(temp.Path, "archive");
                var failedPath = Path.Combine(temp.Path, "log_001.log");
                var nextPath = Path.Combine(temp.Path, "log_002.log");
                var messages = new ConcurrentQueue<string>();
                System.IO.File.WriteAllText(failedPath, "recover me");
                System.IO.File.WriteAllText(nextPath, "next event");
                // A file at the directory path forces the first archive attempt to fail before writing an archive.
                System.IO.File.WriteAllText(target, "prevents directory creation");

                // Pause the failed job inside its diagnostic callback to prove enqueueing returns before the worker finishes.
                // failureObserved signals that the worker reached this point; releaseWorker lets the test resume it.
                SelfLog.Enable(message =>
                {
                    messages.Enqueue(message);
                    failureObserved.Set();
                    releaseWorker.Wait(TimeSpan.FromSeconds(15));
                });

                try
                {
                    // Run enqueueing separately so the timeout also detects an accidental wait for the blocked worker.
                    var enqueue = Task.Run(() => hooks.OnFileDeleting(failedPath));
                    await AwaitWithTimeout(enqueue);
                    failureObserved.Wait(TimeSpan.FromSeconds(15)).ShouldBeTrue();

                    // This task represents the queue so far. The renamed source must remain available for recovery.
                    var firstFlush = hooks.BackgroundRunningArchiveTask();
                    firstFlush.IsCompleted.ShouldBeFalse();
                    var pendingPath = Directory.GetFiles(temp.Path, "*.archive-pending").Single();
                    System.IO.File.ReadAllText(pendingPath).ShouldBe("recover me");
                    messages.Single().ShouldContain(pendingPath);

                    // Remove the obstacle and queue a second file while the first job is still paused.
                    System.IO.File.Delete(target);
                    hooks.OnFileDeleting(nextPath);
                    // The new snapshot includes the second job, which must wait for the first job to finish.
                    var secondFlush = hooks.BackgroundRunningArchiveTask();
                    secondFlush.ShouldNotBeSameAs(firstFlush);
                    secondFlush.IsCompleted.ShouldBeFalse();
                    // The next job cannot overtake the first, even while the first reports a failure.
                    Directory.Exists(target).ShouldBeFalse();

                    releaseWorker.Set();
                    await AwaitWithTimeout(secondFlush);

                    // Failure must not stop later jobs: only the failed source remains, and the second archive is readable.
                    System.IO.File.ReadAllText(pendingPath).ShouldBe("recover me");
                    System.IO.File.Exists(nextPath).ShouldBeFalse();
                    Utils.DecompressLines(Path.Combine(target, "log_002.log.gz")).ShouldBe(new[] { "next event" });
                    Directory.GetFiles(temp.Path, "*.archive-pending").ShouldBe(new[] { pendingPath });
                }
                finally
                {
                    // Release the worker even if an assertion fails, and finish its work before resetting shared SelfLog state.
                    releaseWorker.Set();
                    try
                    {
                        await AwaitWithTimeout(hooks.BackgroundRunningArchiveTask());
                    }
                    finally
                    {
                        SelfLog.Disable();
                    }
                }
            }
        }

        [Fact]
        public void Should_preserve_source_when_rename_fails()
        {
            using (var temp = new TempFolder())
            {
                var hooks = new ArchiveHooks(archiveAsync: true);
                var path = temp.AllocateFilename("log");
                System.IO.File.WriteAllText(path, "keep me");

                // The exclusive handle prevents renaming on Windows; failure must leave the source untouched.
                using (System.IO.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    Should.Throw<IOException>(() => hooks.OnFileDeleting(path));

                System.IO.File.ReadAllText(path).ShouldBe("keep me");
                Directory.GetFiles(temp.Path).ShouldBe(new[] { path });
                hooks.BackgroundRunningArchiveTask().IsCompleted.ShouldBeTrue();
            }
        }

        [Theory]
        [InlineData(false, CompressionLevel.Fastest)]
        [InlineData(true, CompressionLevel.Fastest)]
        [InlineData(false, CompressionLevel.NoCompression)]
        [InlineData(true, CompressionLevel.NoCompression)]
        public async Task Should_replace_existing_archive_without_trailing_bytes(bool archiveAsync, CompressionLevel compressionLevel)
        {
            using (var temp = new TempFolder())
            {
                var target = temp.AllocateFolderName();
                Directory.CreateDirectory(target);
                var path = Path.Combine(temp.Path, "log.log");
                var targetPath = Path.Combine(target, compressionLevel == CompressionLevel.NoCompression ? "log.log" : "log.log.gz");
                System.IO.File.WriteAllText(path, "replacement event");
                // Make the old archive larger than its replacement to detect stale bytes left by a non-truncating write.
                System.IO.File.WriteAllText(targetPath, new string('x', 8192));

                var hooks = new ArchiveHooks(archiveAsync, compressionLevel, target);
                hooks.OnFileDeleting(path);
                await AwaitWithTimeout(hooks.BackgroundRunningArchiveTask());

                new FileInfo(targetPath).Length.ShouldBeLessThan(8192L);
                var lines = compressionLevel == CompressionLevel.NoCompression
                    ? System.IO.File.ReadAllLines(targetPath)
                    : Utils.DecompressLines(targetPath).ToArray();
                lines.ShouldBe(new[] { "replacement event" });
                Directory.GetFiles(target).ShouldBe(new[] { targetPath });
            }
        }

        [Fact]
        public async Task Should_retain_source_when_writing_final_archive_fails()
        {
            using (var temp = new TempFolder())
            {
                var target = temp.AllocateFolderName();
                // A directory at the expected archive filename forces a write failure after the source has been renamed.
                Directory.CreateDirectory(Path.Combine(target, "log.log.gz"));
                var path = Path.Combine(temp.Path, "log.log");
                System.IO.File.WriteAllText(path, "recoverable event");

                var hooks = new ArchiveHooks(archiveAsync: true, targetDirectory: target);
                hooks.OnFileDeleting(path);
                await AwaitWithTimeout(hooks.BackgroundRunningArchiveTask());

                var pendingPath = Directory.GetFiles(temp.Path, "*.archive-pending").Single();
                System.IO.File.ReadAllText(pendingPath).ShouldBe("recoverable event");
                Directory.GetFiles(target).ShouldBeEmpty();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Should_allow_more_archives_after_flushing(bool archiveAsync)
        {
            using (var temp = new TempFolder())
            {
                var hooks = new ArchiveHooks(archiveAsync);
                foreach (var message in new[] { "first event", "second event" })
                {
                    var path = temp.AllocateFilename("log");
                    System.IO.File.WriteAllText(path, message);
                    hooks.OnFileDeleting(path);
                    await AwaitWithTimeout(hooks.BackgroundRunningArchiveTask());

                    Utils.DecompressLines(path + ".gz").ShouldBe(new[] { message });
                }

                hooks.BackgroundRunningArchiveTask().IsCompleted.ShouldBeTrue();
                Directory.GetFiles(temp.Path, "*.gz").Length.ShouldBe(2);
                Directory.GetFiles(temp.Path, "*.archive-pending").ShouldBeEmpty();
            }
        }

        [Fact]
        public async Task Should_keep_pending_sources_out_of_uncompressed_retention()
        {
            using (var temp = new TempFolder())
            {
                var target = temp.AllocateFolderName();
                Directory.CreateDirectory(target);
                // This name would sort ahead of the real archives; retention must exclude pending sources entirely.
                var pendingPath = Path.Combine(target, "log_999.log.abc.archive-pending");
                System.IO.File.WriteAllText(pendingPath, "recoverable source");

                var hooks = new ArchiveHooks(1, archiveAsync: true, CompressionLevel.NoCompression, target);
                foreach (var name in new[] { "log_001.log", "log_002.log" })
                {
                    var path = Path.Combine(temp.Path, name);
                    System.IO.File.WriteAllText(path, name);
                    hooks.OnFileDeleting(path);
                }

                await AwaitWithTimeout(hooks.BackgroundRunningArchiveTask());

                System.IO.File.ReadAllText(pendingPath).ShouldBe("recoverable source");
                Directory.GetFiles(target, "*.log").Select(Path.GetFileName).ShouldBe(new[] { "log_002.log" });
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Should_validate_constructor_arguments(bool archiveAsync)
        {
            Should.Throw<ArgumentException>(() => new ArchiveHooks(archiveAsync, CompressionLevel.NoCompression));
            Should.Throw<ArgumentException>(() => new ArchiveHooks(0, archiveAsync));
            Should.Throw<ArgumentException>(() => new ArchiveHooks(-1, archiveAsync));
            Should.Throw<ArgumentException>(() => new ArchiveHooks(1, archiveAsync, targetDirectory: "{Date:yyyy}"));
        }

        [Fact]
        public void Should_leave_source_for_serilog_in_default_synchronous_mode()
        {
            using (var temp = new TempFolder())
            {
                var hooks = new ArchiveHooks();
                var path = temp.AllocateFilename("log");
                System.IO.File.WriteAllText(path, "synchronous event");

                hooks.OnFileDeleting(path);

                System.IO.File.Exists(path).ShouldBeTrue();
                Utils.DecompressLines(path + ".gz").ShouldBe(new[] { "synchronous event" });
                hooks.BackgroundRunningArchiveTask().IsCompleted.ShouldBeTrue();
            }
        }

        private static async Task AwaitWithTimeout(Task task)
        {
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.ShouldBeSameAs(task);
            await task;
        }
    }
}
