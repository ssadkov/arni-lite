using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Api;
using ArniLite.Storage;

namespace ArniLite.Windows
{
    public sealed class WhisperPackage
    {
        public const string RuntimeVersion = "v1.9.2";
        public const long ModelBytes = 147951465;
        public const string ModelHash = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
        public static readonly Uri ModelUri = new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin");
        private readonly VerifiedDownload _download;
        private readonly string _root;
        private readonly bool _x64;
        public WhisperPackage(VerifiedDownload download, string? root = null, bool? x64 = null)
        {
            _download = download;
            _root = Path.GetFullPath(root ?? Path.Combine(AppPaths.DataDirectory, "speech"));
            _x64 = x64 ?? Environment.Is64BitOperatingSystem;
        }
        public string RuntimeDirectory => Path.Combine(_root, RuntimeVersion + (_x64 ? "-x64" : "-x86"));
        public string Executable => Path.Combine(RuntimeDirectory, "whisper-cli.exe");
        public string Model => Path.Combine(_root, "ggml-base.bin");
        public bool IsInstalled => File.Exists(Executable) && File.Exists(Path.Combine(RuntimeDirectory, "ready")) && File.Exists(Model) && new FileInfo(Model).Length == ModelBytes;
        public long RuntimeBytes => _x64 ? 8194445 : 5189502;
        public string RuntimeHash => _x64 ? "49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a" : "de170719aebcb4794d695d449e179002db1fe03b862f21f5c34b2909a7cf8f22";
        public Uri RuntimeUri => new Uri("https://github.com/ggml-org/whisper.cpp/releases/download/" + RuntimeVersion + "/whisper-bin-" + (_x64 ? "x64" : "Win32") + ".zip");

        public async Task InstallAsync(IProgress<double>? progress, CancellationToken token)
        {
            Directory.CreateDirectory(_root);
            var archive = Path.Combine(_root, "runtime-" + Guid.NewGuid().ToString("N") + ".zip");
            var staging = RuntimeDirectory + "." + Guid.NewGuid().ToString("N") + ".staging";
            try
            {
                await _download.DownloadAsync(RuntimeUri, archive, RuntimeBytes, RuntimeHash, null, token).ConfigureAwait(false);
                await Task.Run(() => ExtractRuntime(archive, staging), token).ConfigureAwait(false);
                foreach (var notice in new[] { "whisper.cpp.txt", "Whisper-model.txt" })
                {
                    using var resource = typeof(WhisperPackage).Assembly.GetManifestResourceStream("ArniLite.Windows.Licenses." + notice);
                    using var output = File.Create(Path.Combine(staging, notice));
                    resource!.CopyTo(output);
                }
                if (!File.Exists(Model)) await _download.DownloadAsync(ModelUri, Model, ModelBytes, ModelHash, progress, token).ConfigureAwait(false);
                else
                {
                    try { await Task.Run(() => VerifiedDownload.VerifyFile(Model, ModelBytes, ModelHash), token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is InvalidDataException || ex is System.Security.Cryptography.CryptographicException)
                    { await _download.DownloadAsync(ModelUri, Model, ModelBytes, ModelHash, progress, token).ConfigureAwait(false); }
                }
                token.ThrowIfCancellationRequested();
                // Paths contain only pinned version/architecture and a generated suffix under the speech directory.
                if (Directory.Exists(RuntimeDirectory)) DeleteOwnedDirectory(RuntimeDirectory);
                Directory.Move(staging, RuntimeDirectory);
                File.WriteAllText(Path.Combine(RuntimeDirectory, "ready"), RuntimeHash);
            }
            finally
            {
                if (File.Exists(archive)) File.Delete(archive);
                if (Directory.Exists(staging)) DeleteOwnedDirectory(staging);
            }
        }

        public static void ExtractRuntime(string archivePath, string destination)
        {
            Directory.CreateDirectory(destination);
            using var stream = File.OpenRead(archivePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                // Extract a fixed, flat executable/DLL allowlist. Never preserve archive paths.
                if (!entry.FullName.StartsWith("Release/", StringComparison.Ordinal)) continue;
                var name = entry.FullName.Substring("Release/".Length);
                if (name.Contains("/") || name.Contains("\\") || !(name == "whisper-cli.exe" || name == "whisper.dll" || (name.StartsWith("ggml", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal)))) continue;
                if (!names.Add(name) || entry.Length > 16 * 1024 * 1024 || (total += entry.Length) > 48 * 1024 * 1024) throw new InvalidDataException("Invalid runtime archive.");
                using var input = entry.Open();
                using var output = new FileStream(Path.Combine(destination, name), FileMode.CreateNew);
                input.CopyTo(output);
            }
            if (!names.Contains("whisper-cli.exe") || !names.Contains("whisper.dll") || !names.Contains("ggml.dll")) throw new InvalidDataException("Incomplete runtime archive.");
        }

        public void Remove()
        {
            if (Directory.Exists(RuntimeDirectory)) DeleteOwnedDirectory(RuntimeDirectory);
            if (File.Exists(Model)) File.Delete(Model);
        }
        private void DeleteOwnedDirectory(string directory)
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Package path is outside the speech directory.");
            EnsureOrdinaryDirectory(_root);
            DeleteOrdinaryDirectory(full);
        }
        private static void EnsureOrdinaryDirectory(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked package directories are not supported.");
        }
        private static void DeleteOrdinaryDirectory(string path)
        {
            EnsureOrdinaryDirectory(path);
            foreach (var file in Directory.GetFiles(path)) File.Delete(file);
            foreach (var child in Directory.GetDirectories(path)) DeleteOrdinaryDirectory(child);
            Directory.Delete(path, false);
        }
    }
}
