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
Install-Package Serilog.Sinks.File.Archive -Version 1.0.0
```

To enable archiving, use one of the new `LoggerSinkConfiguration` extensions that has a `FileLifecycleHooks` argument, and create a new `ArchiveHooks`. For example, to write GZip compressed logs to another directory:

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

### JSON appsettings.json configuration

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

- [serilog-sinks-file-header](https://github.com/cocowalla/serilog-sinks-file-header): writes a header to the start of each log file
- [serilog-sinks-file-gzip](https://github.com/cocowalla/serilog-sinks-file-gzip): compresses logs as they are written, using streaming GZIP compression
