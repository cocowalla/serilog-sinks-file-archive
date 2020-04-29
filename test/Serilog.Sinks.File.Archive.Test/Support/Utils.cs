using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Serilog.Sinks.File.Archive.Tests.Support
{
    public static class Utils
    {
        public static List<string> DecompressLines(string path)
        {
            using (var textStream = new MemoryStream())
            {
                using (var fs = System.IO.File.OpenRead(path))
                using (var decompressStream = new GZipStream(fs, CompressionMode.Decompress))
                {
                    decompressStream.CopyTo(textStream);
                }

                textStream.Position = 0;
                var lines = textStream.ReadAllLines();

                return lines;
            }
        }
        
        public static List<string> ReadAllLines(this Stream stream)
        {
            var lines = new List<string>();

            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
    }
}