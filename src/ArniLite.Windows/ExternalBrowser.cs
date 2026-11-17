using System;
using System.Diagnostics;
using ArniLite.Core;

namespace ArniLite.Windows
{
    public static class ExternalBrowser
    {
        public static void Open(string uri)
        {
            AuthLink.EnsureSafe(uri);
            Start(uri);
        }

        public static void OpenReleases(string uri)
        {
            if (!IsArniLiteReleasePage(uri))
            {
                return;
            }

            Start(uri);
        }

        public static bool IsArniLiteReleasePage(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            if (parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo))
            {
                return false;
            }

            if (!parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = parsed.AbsolutePath.TrimEnd('/');
            return path.Equals("/ssadkov/arni-lite/releases", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/ssadkov/arni-lite/releases/", StringComparison.OrdinalIgnoreCase);
        }

        private static void Start(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                });
            }
            catch (System.Exception)
            {
            }
        }
    }
}
