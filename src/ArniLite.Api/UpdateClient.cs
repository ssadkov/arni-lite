using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArniLite.Api
{
    public sealed class AvailableUpdate
    {
        public UpdateManifest Manifest { get; }
        public UpdateInstaller Installer { get; }
        public Uri DownloadUri { get; }
        public Uri ReleaseUri { get; }
        public AvailableUpdate(UpdateManifest manifest, UpdateInstaller installer, string tag)
        {
            Manifest = manifest; Installer = installer;
            DownloadUri = UpdateClient.AssetUri(tag, installer.FileName);
            ReleaseUri = new Uri("https://github.com/ssadkov/arni-lite/releases/tag/" + Uri.EscapeDataString(tag));
        }
    }

    public sealed class UpdateClient
    {
        private readonly VerifiedDownload _download;
        private readonly string _publicKey;
        public UpdateClient(VerifiedDownload download, string publicKey) { _download = download; _publicKey = publicKey; }
        public static Uri AssetUri(string tag, string name) => new Uri("https://github.com/ssadkov/arni-lite/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));
        public async Task<AvailableUpdate?> CheckAsync(string currentVersion, string architecture, Version windows, bool preview, CancellationToken token)
        {
            var bytes = await _download.ReadBoundedAsync(new Uri("https://api.github.com/repos/ssadkov/arni-lite/releases?per_page=30"), 1024 * 1024, token).ConfigureAwait(false);
            var releases = JArray.Parse(Encoding.UTF8.GetString(bytes));
            AvailableUpdate? best = null;
            foreach (var release in releases.OfType<JObject>())
            {
                if (release.Value<bool?>("draft") == true || (!preview && release.Value<bool?>("prerelease") == true)) continue;
                var tag = release.Value<string>("tag_name") ?? "";
                ReleaseVersion version;
                try { version = ReleaseVersion.Parse(tag); }
                catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is ArgumentException) { continue; }
                if (version.CompareTo(ReleaseVersion.Parse(currentVersion)) <= 0 || (!preview && version.Prerelease.Length > 0)) continue;
                var assets = (release["assets"] as JArray)?.OfType<JObject>().Select(a => a.Value<string>("name")).ToArray() ?? Array.Empty<string>();
                // Legacy releases are never installed without an authenticated manifest.
                if (!assets.Contains("update.json") || !assets.Contains("update.json.sig")) continue;
                var manifestBytes = await _download.ReadBoundedAsync(AssetUri(tag, "update.json"), 64 * 1024, token).ConfigureAwait(false);
                var signature = await _download.ReadBoundedAsync(AssetUri(tag, "update.json.sig"), 1024, token).ConfigureAwait(false);
                SignedUpdate.Verify(manifestBytes, signature, _publicKey);
                var manifest = JsonConvert.DeserializeObject<UpdateManifest>(Encoding.UTF8.GetString(manifestBytes)) ?? throw new FormatException("Missing manifest.");
                if (manifest.Version != version.Text) throw new FormatException("Release version mismatch.");
                var installer = manifest.Select(currentVersion, architecture, windows, preview);
                if (installer == null || !assets.Contains(installer.FileName)) continue;
                if (best == null || version.CompareTo(ReleaseVersion.Parse(best.Manifest.Version)) > 0) best = new AvailableUpdate(manifest, installer, tag);
            }
            return best;
        }
    }
}
