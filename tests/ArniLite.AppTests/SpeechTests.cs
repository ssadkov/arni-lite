using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Windows;
using Xunit;

namespace ArniLite.AppTests
{
    public class SpeechTests
    {
        [Fact]
        public void WaveRecordingHasPcmFormatAndBoundedPayload()
        {
            using var temporary = new SpeechFixture();
            var path = Path.Combine(temporary.Root, "sample.wav");
            new RecordedSpeech(new byte[32000], true).WriteWave(path);
            using var reader = new BinaryReader(File.OpenRead(path));
            Assert.Equal("RIFF", new string(reader.ReadChars(4))); Assert.Equal(32036, reader.ReadInt32());
            Assert.Equal("WAVEfmt ", new string(reader.ReadChars(8))); Assert.Equal(16, reader.ReadInt32());
            Assert.Equal(1, reader.ReadUInt16()); Assert.Equal(1, reader.ReadUInt16()); Assert.Equal(16000, reader.ReadInt32());
            Assert.Equal(32000, reader.ReadInt32()); Assert.Equal(2, reader.ReadUInt16()); Assert.Equal(16, reader.ReadUInt16());
        }

        [Fact]
        public void RuntimeExtractionUsesFlatAllowlistAndIgnoresTraversalAndOtherExecutables()
        {
            using var temporary = new SpeechFixture();
            var zip = Path.Combine(temporary.Root, "package.zip");
            using (var file = File.Create(zip))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                foreach (var name in new[] { "Release/whisper-cli.exe", "Release/whisper.dll", "Release/ggml.dll", "Release/ggml-base.dll", "Release/../outside.exe", "Release/whisper-server.exe", "Release/nested/ggml-evil.dll" })
                    using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("fixture");
            var destination = Path.Combine(temporary.Root, "extracted");
            WhisperPackage.ExtractRuntime(zip, destination);
            Assert.Equal(new[] { "ggml-base.dll", "ggml.dll", "whisper-cli.exe", "whisper.dll" }, Directory.GetFiles(destination).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.False(File.Exists(Path.Combine(temporary.Root, "outside.exe")));
        }

        [Fact]
        public async Task RecognitionIsLocalUsesChosenLanguageAndRemovesRecordingAndTranscript()
        {
            using var fixture = new SpeechFixture(); fixture.MakeInstalled();
            var runner = new SpeechRunner();
            var recognizer = new WhisperRecognizer(fixture.Package, runner, fixture.Temporary);
            var text = await recognizer.RecognizeAsync(new RecordedSpeech(new byte[32000], true), "kk", CancellationToken.None);
            Assert.Equal("Сәлем әлем", text);
            Assert.Contains("-l kk", runner.Arguments); Assert.Contains("-ng", runner.Arguments); Assert.Contains("-nf", runner.Arguments);
            Assert.Equal(fixture.Package.Executable, runner.Executable);
            Assert.Empty(Directory.GetFileSystemEntries(fixture.Temporary));
        }

        [Fact]
        public async Task SilenceNeverStartsRecognizerAndCancellationCleansAudio()
        {
            using var fixture = new SpeechFixture(); fixture.MakeInstalled();
            var runner = new SpeechRunner { Cancel = true };
            var recognizer = new WhisperRecognizer(fixture.Package, runner, fixture.Temporary);
            Assert.Equal("", await recognizer.RecognizeAsync(new RecordedSpeech(new byte[32000], false), "ru", CancellationToken.None));
            Assert.Equal("", runner.Executable);
            await Assert.ThrowsAsync<OperationCanceledException>(() => recognizer.RecognizeAsync(new RecordedSpeech(new byte[32000], true), "ru", CancellationToken.None));
            Assert.Empty(Directory.GetFileSystemEntries(fixture.Temporary));
        }

        private sealed class SpeechFixture : IDisposable
        {
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "arni-speech-" + Guid.NewGuid().ToString("N"));
            public string Temporary => Path.Combine(Root, "temporary");
            public WhisperPackage Package { get; }
            private readonly HttpClient _http = new HttpClient(new NoNetwork());
            public SpeechFixture() { Directory.CreateDirectory(Root); Package = new WhisperPackage(new VerifiedDownload(_http), Path.Combine(Root, "speech"), true); }
            public void MakeInstalled()
            {
                Directory.CreateDirectory(Package.RuntimeDirectory); File.WriteAllText(Package.Executable, "fixture"); File.WriteAllText(Path.Combine(Package.RuntimeDirectory, "ready"), "fixture");
                using var model = File.Create(Package.Model); model.SetLength(WhisperPackage.ModelBytes);
            }
            public void Dispose() { _http.Dispose(); Delete(Root); }
            private static void Delete(string directory) { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); foreach (var child in Directory.GetDirectories(directory)) Delete(child); Directory.Delete(directory); }
        }
        private sealed class SpeechRunner : IProcessRunner
        {
            public string Arguments = "", Executable = ""; public bool Cancel;
            public Task<ProcessRunResult> RunPowerShellAsync(string command, int timeoutMs, CancellationToken cancellationToken) => throw new InvalidOperationException("Speech must not use PowerShell.");
            public Task<ProcessRunResult> RunProcessAsync(string fileName, string arguments, string input, string workingDirectory, int timeoutMs, CancellationToken token)
            {
                Executable = fileName; Arguments = arguments;
                Assert.Equal("", input); Assert.True(File.Exists(Regex.Match(arguments, "-f \"([^\"]+)\"").Groups[1].Value));
                if (Cancel) throw new OperationCanceledException();
                File.WriteAllText(Regex.Match(arguments, "-of \"([^\"]+)\"").Groups[1].Value + ".txt", "Сәлем әлем");
                return Task.FromResult(new ProcessRunResult { Status = "completed", ExitCode = 0 });
            }
        }
        private sealed class NoNetwork : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("Unexpected network access.");
        }
    }
}
