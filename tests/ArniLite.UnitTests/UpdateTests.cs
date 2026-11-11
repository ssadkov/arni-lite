using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ArniLite.UnitTests
{
    public class UpdateTests
    {
        [Theory]
        [InlineData("0.1.2", "0.1.2-preview", 1)]
        [InlineData("0.1.2-preview.10", "0.1.2-preview.9", 1)]
        [InlineData("0.1.3-preview", "0.1.2", 1)]
        [InlineData("v0.1.2+abc", "0.1.2", 0)]
        [InlineData("0.1.2-alpha", "0.1.2-beta", -1)]
        public void ReleaseOrdering(string left, string right, int sign) => Assert.Equal(sign, Math.Sign(ReleaseVersion.Parse(left).CompareTo(ReleaseVersion.Parse(right))));

        [Theory]
        [InlineData("latest")]
        [InlineData("0.1")]
        [InlineData("01.1.2")]
        [InlineData("0.1.2-preview.01")]
        public void InvalidVersionsAreRejected(string value) => Assert.Throws<FormatException>(() => ReleaseVersion.Parse(value));

        [Fact]
        public void CompatibilityChannelsArchitectureAndDowngradesAreEnforced()
        {
            var manifest = Manifest("0.2.0");
            Assert.NotNull(manifest.Select("0.1.2", "x64", new Version(6, 1, 7601), false));
            Assert.Null(manifest.Select("0.2.0", "x64", new Version(10, 0, 1), false));
            Assert.Null(manifest.Select("0.3.0", "x64", new Version(10, 0, 1), false));
            Assert.Null(manifest.Select("0.1.2", "x86", new Version(10, 0, 1), false));
            Assert.Null(manifest.Select("0.1.2", "x64", new Version(6, 1, 7600), false));
            var preview = Manifest("0.2.0-preview.1");
            Assert.Null(preview.Select("0.1.2", "x64", new Version(10, 0, 1), false));
            Assert.NotNull(preview.Select("0.1.2", "x64", new Version(10, 0, 1), true));
            manifest.Installers[0].FileName = "../../foreign.exe";
            Assert.Throws<FormatException>(() => manifest.Select("0.1.2", "x64", new Version(10, 0, 1), false));
        }

        [Fact]
        public async Task SignedReleaseIsAuthenticatedAndDownloadOriginIsFixed()
        {
            using var signer = RSA.Create(2048);
            var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Manifest("0.2.0")));
            var signature = signer.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var http = new HttpClient(new FeedHandler(data, signature));
            var update = await new UpdateClient(new VerifiedDownload(http), PublicXml(signer)).CheckAsync("0.1.2", "x64", new Version(10, 0, 1), false, CancellationToken.None);
            Assert.NotNull(update);
            Assert.Equal("https://github.com/ssadkov/arni-lite/releases/download/v0.2.0/ArniLiteSetup-0.2.0-x64.exe", update!.DownloadUri.ToString());
            data[5] ^= 1;
            await Assert.ThrowsAsync<CryptographicException>(() => new UpdateClient(new VerifiedDownload(http), PublicXml(signer)).CheckAsync("0.1.2", "x64", new Version(10, 0, 1), false, CancellationToken.None));
        }

        [Fact]
        public async Task InvalidSignatureAndReleaseMismatchNeverProduceAnUpdate()
        {
            using var signer = RSA.Create(2048);
            using var other = RSA.Create(2048);
            var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Manifest("0.3.0")));
            var signature = signer.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var http = new HttpClient(new FeedHandler(data, signature));
            var download = new VerifiedDownload(http);
            await Assert.ThrowsAsync<CryptographicException>(() => new UpdateClient(download, PublicXml(other)).CheckAsync("0.1.2", "x64", new Version(10, 0, 1), false, CancellationToken.None));
            await Assert.ThrowsAsync<FormatException>(() => new UpdateClient(download, PublicXml(signer)).CheckAsync("0.1.2", "x64", new Version(10, 0, 1), false, CancellationToken.None));
        }

        [Fact]
        public async Task UnauthenticatedLegacyReleaseIsSkipped()
        {
            using var http = new HttpClient(new FeedHandler(Array.Empty<byte>(), Array.Empty<byte>(), legacy: true));
            Assert.Null(await new UpdateClient(new VerifiedDownload(http), UpdateTrust.PublicKeyXml).CheckAsync("0.1.0", "x64", new Version(10, 0, 1), false, CancellationToken.None));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CorruptionAndTruncationPreserveExistingFileAndLeaveNoPartialDownload(bool truncated)
        {
            var directory = Path.Combine(Path.GetTempPath(), "arni-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "download.bin"); File.WriteAllText(path, "existing");
                var bytes = Encoding.UTF8.GetBytes("payload");
                using var http = new HttpClient(new BinaryHandler(bytes));
                var download = new VerifiedDownload(http);
                await Assert.ThrowsAnyAsync<Exception>(() => download.DownloadAsync(new Uri("https://example.test/file"), path, bytes.Length + (truncated ? 1 : 0), new string('0', 64), null, CancellationToken.None));
                Assert.Equal("existing", File.ReadAllText(path));
                Assert.Equal(new[] { "download.bin" }, Directory.GetFiles(directory).Select(Path.GetFileName));
            }
            finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }

        [Fact]
        public async Task ValidDownloadIsVerifiedAndCancellationLeavesNoFiles()
        {
            var directory = Path.Combine(Path.GetTempPath(), "arni-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var bytes = Encoding.UTF8.GetBytes("payload");
                using var http = new HttpClient(new BinaryHandler(bytes));
                var download = new VerifiedDownload(http);
                var path = Path.Combine(directory, "download.bin");
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                await download.DownloadAsync(new Uri("https://example.test/file"), path, bytes.Length, hash, null, CancellationToken.None);
                Assert.Equal(bytes, File.ReadAllBytes(path)); File.Delete(path);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.DownloadAsync(new Uri("https://example.test/file"), path, bytes.Length, hash, null, cancelled.Token));
                Assert.Empty(Directory.GetFiles(directory));
            }
            finally { Directory.Delete(directory); }
        }

        [Fact]
        public void OldPreferencesRetainSafeDefaultsAndNewOptionsPersist()
        {
            var path = Path.Combine(Path.GetTempPath(), "arni-prefs-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"Language\":\"en\",\"WrapText\":true}");
                var store = new PreferencesStore(path);
                Assert.True(store.Value.CheckUpdatesAutomatically); Assert.False(store.Value.PreviewUpdates); Assert.Equal("ru", store.Value.DictationLanguage);
                store.Value.CheckUpdatesAutomatically = false; store.Value.PreviewUpdates = true; store.Value.DictationLanguage = "kk"; store.Value.MicrophoneId = 2;
                store.Save(); var restored = new PreferencesStore(path);
                Assert.False(restored.Value.CheckUpdatesAutomatically); Assert.True(restored.Value.PreviewUpdates); Assert.Equal("kk", restored.Value.DictationLanguage); Assert.Equal(2, restored.Value.MicrophoneId);
            }
            finally { File.Delete(path); }
        }

        private static UpdateManifest Manifest(string version) => new UpdateManifest { Schema = 1, Version = version, MinimumWindows = "6.1.7601", Installers = new System.Collections.Generic.List<UpdateInstaller> { new UpdateInstaller { Architecture = "x64", FileName = "ArniLiteSetup-" + version + "-x64.exe", Bytes = 42, Sha256 = new string('a', 64) } } };
        private static string PublicXml(RSA rsa) { var key = rsa.ExportParameters(false); return "<RSAKeyValue><Modulus>" + Convert.ToBase64String(key.Modulus!) + "</Modulus><Exponent>" + Convert.ToBase64String(key.Exponent!) + "</Exponent></RSAKeyValue>"; }
        private sealed class FeedHandler : HttpMessageHandler
        {
            private readonly byte[] _manifest, _signature; private readonly bool _legacy;
            public FeedHandler(byte[] manifest, byte[] signature, bool legacy = false) { _manifest = manifest; _signature = signature; _legacy = legacy; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var url = request.RequestUri!.ToString();
                var bytes = url.Contains("api.github.com") ? Encoding.UTF8.GetBytes(new JArray(new JObject { ["tag_name"] = "v0.2.0", ["assets"] = _legacy ? new JArray() : new JArray(new JObject { ["name"] = "update.json" }, new JObject { ["name"] = "update.json.sig" }, new JObject { ["name"] = "ArniLiteSetup-0.2.0-x64.exe", ["browser_download_url"] = "https://untrusted.example/foreign.exe" }) }).ToString()) : url.EndsWith(".sig") ? _signature : _manifest;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
        }
        private sealed class BinaryHandler : HttpMessageHandler
        {
            private readonly byte[] _bytes; public BinaryHandler(byte[] bytes) { _bytes = bytes; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) }); }
        }
    }
}
