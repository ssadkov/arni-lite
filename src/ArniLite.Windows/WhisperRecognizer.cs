using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Core;
using ArniLite.Storage;

namespace ArniLite.Windows
{
    public sealed class WhisperRecognizer
    {
        private readonly WhisperPackage _package;
        private readonly IProcessRunner _runner;
        private readonly string _temporaryRoot;
        public WhisperRecognizer(WhisperPackage package, IProcessRunner runner, string? temporaryRoot = null)
        {
            _package = package; _runner = runner; _temporaryRoot = temporaryRoot ?? Path.Combine(AppPaths.DataDirectory, "speech", "temporary");
            CleanAbandonedRecordings();
        }

        private void CleanAbandonedRecordings()
        {
            try
            {
                if (!Directory.Exists(_temporaryRoot) || (File.GetAttributes(_temporaryRoot) & FileAttributes.ReparsePoint) != 0) return;
                foreach (var directory in Directory.GetDirectories(_temporaryRoot))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var name in new[] { "input.wav", "transcript.txt" })
                    {
                        var path = Path.Combine(directory, name);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    if (Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory, false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { DiagnosticLog.Current.Write("speech_cleanup_failed", exception: ex); }
        }

        public async Task<string> RecognizeAsync(RecordedSpeech recording, string language, CancellationToken token)
        {
            if (!recording.HasSignal || recording.Pcm.Length < 3200) return "";
            if (recording.Pcm.Length > MicrophoneRecorder.MaximumSeconds * 32000) throw new ArgumentException("Recording too long.");
            if (language != "ru" && language != "en" && language != "kk" && language != "auto") throw new ArgumentException("Unsupported language.");
            if (!_package.IsInstalled) throw new InvalidOperationException("Whisper is not installed.");
            var directory = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var wav = Path.Combine(directory, "input.wav");
            var output = Path.Combine(directory, "transcript");
            try
            {
                recording.WriteWave(wav);
                var threads = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
                var args = "-m " + Quote(_package.Model) + " -f " + Quote(wav) + " -l " + language + " -t " + threads + " -otxt -of " + Quote(output) + " -nt -np -ng -nf -sns";
                var result = await _runner.RunProcessAsync(_package.Executable, args, "", _package.RuntimeDirectory, 10 * 60 * 1000, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result.Status != "completed" || result.ExitCode != 0) throw new InvalidOperationException("Local speech recognition failed.");
                var textPath = output + ".txt";
                if (!File.Exists(textPath) || new FileInfo(textPath).Length > 128 * 1024) throw new InvalidDataException("Missing or oversized transcript.");
                return File.ReadAllText(textPath, Encoding.UTF8).Trim();
            }
            finally
            {
                Array.Clear(recording.Pcm, 0, recording.Pcm.Length);
                // Only known files created for this recording; no recursive cleanup of arbitrary paths.
                foreach (var name in new[] { wav, output + ".txt" }) if (File.Exists(name)) File.Delete(name);
                Directory.Delete(directory, false);
            }
        }
        private static string Quote(string path) => "\"" + path.Replace("\"", "") + "\"";
    }
}
