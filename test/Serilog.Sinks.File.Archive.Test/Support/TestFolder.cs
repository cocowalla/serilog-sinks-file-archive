using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace Serilog.Sinks.File.Archive.Tests.Support
{
    internal class TempFolder : IDisposable
    {
        private static readonly Guid Session = Guid.NewGuid();
        public string Path { get; }

        public TempFolder(string name = null)
        {
            this.Path = System.IO.Path.Combine(
                Environment.GetEnvironmentVariable("TMP") ?? Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp",
                "Serilog.Sinks.File.Archive.Tests",
                Session.ToString("n"),
                name ?? Guid.NewGuid().ToString("n"));

            Directory.CreateDirectory(this.Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(this.Path))
                    Directory.Delete(this.Path, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        public static TempFolder ForCaller([CallerMemberName] string caller = null, [CallerFilePath] string sourceFileName = "")
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (sourceFileName == null) throw new ArgumentNullException(nameof(sourceFileName));

            var folderName = System.IO.Path.GetFileNameWithoutExtension(sourceFileName) + "_" + caller;

            return new TempFolder(folderName);
        }

        public string AllocateFilename(string ext = null)
            => System.IO.Path.Combine(this.Path, Guid.NewGuid().ToString("n") + "." + (ext ?? "tmp"));

        public string AllocateFolderName()
            => System.IO.Path.Combine(this.Path, Guid.NewGuid().ToString("n"));
    }
}
