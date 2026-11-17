using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Core;

namespace ArniLite
{
    public static class AboutReadme
    {
        private const int MaxBytes = 65536;
        private static readonly string[] GitHub =
        {
            "https://raw.githubusercontent.com/ssadkov/arni/main/README.md",
            "https://raw.githubusercontent.com/ssadkov/arni-lite/main/README.md",
        };

        public static string Offline(string baseDirectory)
        {
            return ReadmeText.Prefer(string.Empty, Local(baseDirectory), Embedded());
        }

        public static async Task<string> LoadAsync(string baseDirectory, CancellationToken cancellationToken)
        {
            var github = await FromGitHubAsync(cancellationToken).ConfigureAwait(false);
            return ReadmeText.Prefer(github, Local(baseDirectory), Embedded());
        }

        public static string Local(string baseDirectory)
        {
            var path = Find(baseDirectory);
            if (path == null)
            {
                return string.Empty;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaxBytes)
                {
                    return string.Empty;
                }

                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static async Task<string> FromGitHubAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Arni/" + AppVersion.Display);
                    for (var i = 0; i < GitHub.Length; i++)
                    {
                        var text = await ReadAsync(client, GitHub[i], cancellationToken).ConfigureAwait(false);
                        if (ReadmeText.Normalize(text).Length > 0)
                        {
                            return text;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
            {
            }

            return string.Empty;
        }

        private static async Task<string> ReadAsync(HttpClient client, string url, CancellationToken cancellationToken)
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (media.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return string.Empty;
                }

                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var buffer = new byte[MaxBytes];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var count = await stream.ReadAsync(buffer, read, buffer.Length - read, cancellationToken).ConfigureAwait(false);
                        if (count == 0)
                        {
                            break;
                        }

                        read += count;
                    }

                    return read == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
        }

        private static string Embedded()
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ArniLite.README.md");
            if (stream == null)
            {
                return string.Empty;
            }

            using (stream)
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string Find(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return null;
            }

            var beside = Path.Combine(baseDirectory, "README.md");
            if (File.Exists(beside))
            {
                return beside;
            }

            var dir = baseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "ArniLite.sln")) && File.Exists(Path.Combine(dir, "README.md")))
                {
                    return Path.Combine(dir, "README.md");
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }
    }
}
