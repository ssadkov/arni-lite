using System;

namespace ArniLite.Core
{
    public sealed class McpServerRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool Connected { get; set; }
    }

    public sealed class McpEndpoint
    {
        public string Url { get; set; } = string.Empty;
        public string Authorization { get; set; } = string.Empty;
    }

    public static class McpAddress
    {
        public const int MaxServers = 8;

        public static string Host(string value)
        {
            var normalized = Normalize(value);
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            return new Uri(normalized).Host;
        }

        public static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 300)
            {
                return string.Empty;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return string.Empty;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return string.Empty;
            }

            return uri.AbsoluteUri;
        }
    }
}
