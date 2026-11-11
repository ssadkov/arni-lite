using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Resources;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Storage;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ArniLite.UnitTests
{
    public class LocalizationAndDiagnosticsTests
    {
        [Fact]
        public void EveryLanguageHasTheSameKeysAndCompatiblePlaceholders()
        {
            var manager = new ResourceManager("ArniLite.Core.Strings", typeof(StringCatalog).Assembly);
            var baseline = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).OrderBy(key => key).ToArray();
            foreach (var language in new[] { "en", "kk" })
            {
                var translated = manager.GetResourceSet(CultureInfo.GetCultureInfo(language), true, false)!;
                Assert.Equal(baseline, translated.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).OrderBy(key => key));
                foreach (var key in baseline)
                {
                    var text = translated.GetString(key)!;
                    Assert.False(string.IsNullOrWhiteSpace(text), key);
                    string.Format(CultureInfo.InvariantCulture, text, "one", "two", "three");
                }
            }
        }

        [Fact]
        public void PreferencesPersistLanguageAndWrappingAndRecoverFromCorruption()
        {
            var directory = TempDirectory();
            try
            {
                var path = Path.Combine(directory, "preferences.json");
                var store = new PreferencesStore(path);
                Assert.True(store.Value.WrapText);
                store.Value.Language = "kk";
                store.Value.WrapText = false;
                store.Save();
                var restored = new PreferencesStore(path);
                Assert.Equal("kk", restored.Value.Language);
                Assert.False(restored.Value.WrapText);
                File.WriteAllText(path, "broken");
                var recovered = new PreferencesStore(path);
                Assert.Equal("ru", recovered.Value.Language);
                Assert.True(recovered.Value.WrapText);
                recovered.Save();
                Assert.True(new PreferencesStore(path).Value.WrapText);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void DiagnosticArchiveExcludesPrivateDataAndExceptionMessages()
        {
            var directory = TempDirectory();
            try
            {
                const string secret = "private-token-and-chat-content";
                File.WriteAllText(Path.Combine(directory, "token.bin"), secret);
                File.WriteAllText(Path.Combine(directory, "crash.log"), secret);
                Directory.CreateDirectory(Path.Combine(directory, "chats"));
                File.WriteAllText(Path.Combine(directory, "chats", "a.jsonl"), secret);
                var log = new DiagnosticLog(Path.Combine(directory, "logs"));
                log.Write("account_refresh_failed", exception: new InvalidOperationException(secret), httpStatus: 503);
                var destination = Path.Combine(directory, "support.zip");
                log.Export(destination, "version=0.1.2-preview\narchitecture=x64");
                using var archive = ZipFile.OpenRead(destination);
                Assert.Equal(new[] { "environment.txt", "app.jsonl", "previous.jsonl" }, archive.Entries.Select(entry => entry.FullName));
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    Assert.DoesNotContain(secret, reader.ReadToEnd());
                }
                Assert.Contains("InvalidOperationException", log.ReadRecent());
                Assert.Contains("503", log.ReadRecent());
            }
            finally { Directory.Delete(directory, true); }
        }

        [Theory]
        [InlineData("sample.MP4", PreviewKind.Video)]
        [InlineData("clip.webm", PreviewKind.Video)]
        [InlineData("audio.wav", PreviewKind.Audio)]
        [InlineData("audio.mp3", PreviewKind.Audio)]
        [InlineData("page.html", PreviewKind.Text)]
        [InlineData("notes.md", PreviewKind.Text)]
        public void PreviewClassificationDoesNotRequireReadingTheFile(string name, PreviewKind expected) => Assert.Equal(expected, PreviewPolicy.Classify(name));

        private static string TempDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "arni-preferences-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
