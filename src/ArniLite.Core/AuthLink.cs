using System;

namespace ArniLite.Core
{
    public static class ArniEndpoints
    {
        public const string ApiOrigin = "https://api.arnion.ru";
        public const string YandexStart = "https://api.arnion.ru/api/web/auth/yandex/start";
        public const string GoogleStart = "https://api.arnion.ru/api/web/auth/google/start";
    }

    public sealed class AuthLinkException : Exception
    {
        public AuthLinkException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Accepts only HTTPS links on the Arni hosts, plus loopback for local development.
    /// Rejects device secrets and tokens in the query or fragment.
    /// </summary>
    public static class AuthLink
    {
        public static void EnsureSafe(string uri)
        {
            if (!TryValidate(uri, out var error))
            {
                throw new AuthLinkException(error ?? "Sign-in link is invalid.");
            }
        }

        public static bool TryValidate(string uri, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                error = "Sign-in link is invalid.";
                return false;
            }

            var loopback = parsed.IsLoopback;
            if (parsed.Scheme != Uri.UriSchemeHttps && !(loopback && parsed.Scheme == Uri.UriSchemeHttp))
            {
                error = "Sign-in link must use HTTPS.";
                return false;
            }

            if (!string.IsNullOrEmpty(parsed.UserInfo) || !HostAllowed(parsed))
            {
                error = "Sign-in link host is not allowed.";
                return false;
            }

            if (ContainsSecret(parsed.Query) || ContainsSecret(parsed.Fragment))
            {
                error = "Sign-in link contains a secret.";
                return false;
            }

            return true;
        }

        private static bool HostAllowed(Uri uri)
        {
            if (uri.IsLoopback)
            {
                return true;
            }

            return uri.Host.Equals("chat.arnion.kz", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("api.arnion.ru", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsSecret(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return value.IndexOf("device_code=", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("access_token=", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("id_token=", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("token=", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
