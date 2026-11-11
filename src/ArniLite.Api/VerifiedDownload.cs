using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ArniLite.Api
{
    // No application/authentication client is reused for public binary downloads.
    public sealed class VerifiedDownload
    {
        private readonly HttpClient _http;
        public VerifiedDownload(HttpClient http) { _http = http; }
        public async Task DownloadAsync(Uri source, string destination, long expectedBytes, string sha256, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (source.Scheme != Uri.UriSchemeHttps || expectedBytes <= 0 || expectedBytes > 512L * 1024 * 1024) throw new ArgumentException("Invalid download.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using var response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != expectedBytes) throw new InvalidDataException("Unexpected download size.");
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536]; long total = 0; var reported = -1;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (count == 0) break;
                        total += count;
                        if (total > expectedBytes) throw new InvalidDataException("Download too large.");
                        await output.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
                        var percent = (int)(total * 100 / expectedBytes);
                        if (percent != reported) { reported = percent; progress?.Report(percent); }
                    }
                    if (total != expectedBytes) throw new InvalidDataException("Incomplete download.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(() => VerifyFile(temporary, expectedBytes, sha256), cancellationToken).ConfigureAwait(false);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static void VerifyFile(string path, long bytes, string sha256)
        {
            using var input = File.OpenRead(path);
            if (input.Length != bytes) throw new InvalidDataException("Unexpected file size.");
            using var algorithm = SHA256.Create();
            var actual = BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "");
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("File checksum mismatch.");
        }

        public async Task<byte[]> ReadBoundedAsync(Uri uri, int maximum, CancellationToken token)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("HTTPS required.");
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Response too large.");
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > maximum) throw new InvalidDataException("Response too large.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
    }
}
