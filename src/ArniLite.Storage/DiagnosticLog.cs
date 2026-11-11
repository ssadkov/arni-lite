using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArniLite.Storage
{
    // Only structured technical fields are logged. Exception messages, paths and chat data are excluded.
    public sealed class DiagnosticLog
    {
        private const int MaxBytes = 512 * 1024;
        private readonly object _gate = new object();
        private readonly string _directory;
        private string LogPath => Path.Combine(_directory, "app.jsonl");
        public static DiagnosticLog Current { get; } = new DiagnosticLog(Path.Combine(AppPaths.DataDirectory, "logs"));

        public DiagnosticLog(string directory) { _directory = directory; }

        public void Write(string eventName, string? phase = null, Exception? exception = null, int? httpStatus = null)
        {
            try
            {
                var record = new JObject
                {
                    ["utc"] = DateTimeOffset.UtcNow.ToString("o"), ["event"] = eventName,
                    ["phase"] = phase, ["exceptionType"] = exception?.GetType().FullName,
                    ["hresult"] = exception?.HResult, ["httpStatus"] = httpStatus,
                }.ToString(Formatting.None) + Environment.NewLine;
                lock (_gate)
                {
                    Directory.CreateDirectory(_directory);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    {
                        var previous = Path.Combine(_directory, "previous.jsonl");
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(LogPath, previous);
                    }
                    File.AppendAllText(LogPath, record, new UTF8Encoding(false));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        public string ReadRecent()
        {
            lock (_gate) return ReadBounded(LogPath, 128 * 1024);
        }

        public void Export(string destination, string environment)
        {
            // Fixed allowlist: never include the token store, histories, tool journal, or legacy crash.log.
            lock (_gate)
            using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Add(zip, "environment.txt", environment);
                Add(zip, "app.jsonl", ReadBounded(LogPath, MaxBytes));
                Add(zip, "previous.jsonl", ReadBounded(Path.Combine(_directory, "previous.jsonl"), MaxBytes));
            }
        }

        private static void Add(ZipArchive archive, string name, string text)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }

        private static string ReadBounded(string path, int limit)
        {
            if (!File.Exists(path)) return string.Empty;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var truncated = file.Length > limit;
            if (truncated) file.Seek(-limit, SeekOrigin.End);
            using var reader = new StreamReader(file, Encoding.UTF8);
            if (truncated) reader.ReadLine();
            return reader.ReadToEnd();
        }
    }
}
