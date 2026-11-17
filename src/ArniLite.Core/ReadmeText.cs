using System;

namespace ArniLite.Core
{
    public static class ReadmeText
    {
        public const int MaxChars = 20000;

        public static string Prefer(string github, string local, string embedded)
        {
            var remote = Normalize(github);
            if (remote.Length > 0)
            {
                return remote;
            }

            var file = Normalize(local);
            if (file.Length > 0)
            {
                return file;
            }

            return Normalize(embedded);
        }

        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var value = text.Trim();
            if (value.StartsWith("<", StringComparison.Ordinal) || value.IndexOf("#", StringComparison.Ordinal) < 0)
            {
                return string.Empty;
            }

            if (value.Length <= MaxChars)
            {
                return value;
            }

            return value.Substring(0, MaxChars) + "…";
        }
    }
}
