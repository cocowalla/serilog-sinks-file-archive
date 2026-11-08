# Serilog.Sinks.File.Archive
[![NuGet](https://img.shields.io/nuget/v/Serilog.Sinks.File.Archive.svg)](https://www.nuget.org/packages/Serilog.Sinks.File.Archive)
[![Build status](https://ci.appveyor.com/api/projects/status/iljyx2bcv722aqhx?svg=true)](https://ci.appveyor.com/project/cocowalla/serilog-sinks-file-archive)

A `FileLifecycleHooks`-based plugin for the [Serilog File Sink](https://github.com/serilog/serilog-sinks-file) that works with rolling log files, archiving completed log files before they are deleted by Serilog's retention mechanism.

The following archive methods are supported:

- Compress logs in the same directory (using GZip compression)
- Copying logs to another directory
- Compress logs (using GZip compression) and write them to another directory

### Getting started
To get started, install the latest [Serilog.Sinks.File.Archive](https://www.nuget.org/packages/Serilog.Sinks.File.Archive) package from NuGet:

```powershell
Install-Package Serilog.Sinks.File.Archive
```

To enable archiving, use one of the new `LoggerSinkConfiguration` extensions that has a `FileLifecycleHooks` argument, and create a new `ArchiveHooks`. For example, to write GZip compressed logs to another directory (the directory will be created if it doesn't already exist):

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.File("my-app.log", hooks: new ArchiveHooks(CompressionLevel.Fastest, "C:\\My\\Archive\\Path"))
    .CreateLogger();
```

Or to copy logs as-is to another directory:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.File("my-app.log", hooks: new ArchiveHooks(CompressionLevel.NoCompression, "C:\\My\\Archive\\Path"))
    .CreateLogger();
```

Or to write GZip compressed logs to the same directory the logs are written to:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.File("my-app.log", hooks: new ArchiveHooks(CompressionLevel.Fastest))
    .CreateLogger();
```

Note that archival only works with *rolling* log files, as files are only deleted by Serilog's rolling file retention mechanism.

As is [standard with Serilog](https://github.com/serilog/serilog/wiki/Lifecycle-of-Loggers#in-all-apps), it's important to call `Log.CloseAndFlush();` before your application ends.

### Background Archiving
Archiving runs synchronously by default. To move compression and copying off the logging thread, opt in with `archiveAsync: true`:

```csharp
var hooks = new ArchiveHooks(
    archiveAsync: true,
    compressionLevel: CompressionLevel.Fastest,
    targetDirectory: "C:\\My\\Archive\\Path");

Log.Logger = new LoggerConfiguration()
    .WriteTo.File("my-app.log",
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        retainedFileCountLimit: 2,
        hooks: hooks)
    .CreateLogger();

Log.Information("Hello from the application");
```

The hook synchronously renames each obsolete file to `{originalPath}.{guid}.archive-pending` in its source directory. The rename stays on the source filesystem even if the archive directory is on another volume. Archive filenames remain the same as in synchronous mode, and the pending source is deleted only after archiving succeeds.

Jobs run one at a time per hook instance, in queue order. The queue has no size limit: if archiving falls behind, pending files consume disk space until processed. Directory tokens are resolved when a file is queued. `CompressionLevel.NoCompression` also supports background copying, but its target must differ from the original path.

To limit the number of archived files, use the overload with `retainedFileCountLimit`:

```csharp
new ArchiveHooks(retainedFileCountLimit: 10, archiveAsync: true,
    compressionLevel: CompressionLevel.Fastest, targetDirectory: "C:\\My\\Archive\\Path")
```

Archive retention runs after each successful archive. As with synchronous archiving, a target directory containing date tokens cannot be combined with an archive count limit.

Compression or copying writes directly to the final archive file. A lock shared by hook instances protects destination writes and retention so another job cannot overwrite or delete an archive while it is being written. Renaming source logs and deleting pending sources happen outside that archive lock. The `.archive-pending` sources are excluded from retention and remain available for manual recovery if archiving is interrupted.

**Interrupted compression:** Any aborted background compression leaves the uncompressed source file at its renamed `{originalPath}.{guid}.archive-pending` path for manual recovery, including when the application exits before compression finishes. Background jobs are not automatically awaited at shutdown. You can optionally call `await hooks.BackgroundRunningArchiveTask()` to wait for jobs queued before that call.

**Failures and recovery:** A failed rename is reported synchronously, allowing Serilog to retain the original file. Background failures are reported through [Serilog's `SelfLog`](https://github.com/serilog/serilog/wiki/Debugging-and-Diagnostics) and leave the pending source in place. `BackgroundRunningArchiveTask()` gives a task that waits for jobs to finish but does not propagate background archive failures. Pending sources are excluded from archive retention and are not retried automatically. Recover them manually using the original filename embedded in the pending filename. A failed archive may leave an incomplete destination, so use the retained source for recovery.

The original path no longer exists after a background archive is queued. If combining lifecycle hooks, run any hook that needs the source path before `ArchiveHooks`.

### Token Replacement
The `targetDirectory` constructor parameter supports replacement of tokens at runtime.

Tokens take the form `{Name:FormatString}`, where `Name` is the name of a supported token, and `FormatString` defines how the token value should be formatted.

At present, 2 tokens are supported, `UtcDate` and `Date`. These use [standard .NET date format strings](https://docs.microsoft.com/en-us/dotnet/standard/base-types/custom-date-and-time-format-strings) to insert components of the current date/time into the path. For example, you may wish to organise archived files into folders based on the current **year** and **month**:

```csharp
new ArchiveHooks(CompressionLevel.Fastest, "C:\\Archive\\{UtcDate:yyyy}\\{UtcDate:MM}")
```

### JSON appsettings.json Configuration
It's also possible to enable archival when configuring Serilog from a configuration file using [Serilog.Settings.Configuration](https://github.com/serilog/serilog-settings-configuration/). To do this, you will first need to create a public static class that can provide the configuration system with a configured instance of `ArchiveHooks`:

```csharp
using Serilog.Sinks.File.Archive;

namespace MyApp.Logging
{
    public class SerilogHooks
    {
        public static ArchiveHooks MyArchiveHooks => new ArchiveHooks(CompressionLevel.Fastest, "C:\\My\\Archive\\Path");
    }
}
```

The `hooks` argument in Your `appsettings.json` file should be configured as follows:

```json
{
  "Serilog": {
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "my-app.log",
          "fileSizeLimitBytes": 10485760,
          "rollOnFileSizeLimit": true,
          "retainedFileCountLimit": 5,
          "hooks": "MyApp.Logging.SerilogHooks::MyArchiveHooks, MyApp"
        }
      }
    ]
  }
}
```

To break this down a bit, what you are doing is specifying the fully qualified type name of the static class that provides your `ArchiveHooks`, using `Serilog.Settings.Configuration`'s special `::` syntax to point to the `MyArchiveHooks` member.

### About `FileLifecycleHooks`
`FileLifecycleHooks` is a Serilog File Sink mechanism that allows hooking into log file lifecycle events, enabling scenarios such as wrapping the Serilog output stream in another stream, or capturing files before they are deleted by Serilog's retention mechanism.

Other available hooks include:

- [serilog-sinks-file-header](https://github.com/cocowalla/serilog-sinks-file-header): writes a header to the start of
  each log file
- [serilog-sinks-file-gzip](https://github.com/cocowalla/serilog-sinks-file-gzip): compresses logs as they are written,
  using streaming GZIP compression
