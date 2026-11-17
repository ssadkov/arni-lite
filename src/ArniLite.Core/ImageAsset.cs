using System;
using System.Security.Cryptography;
using System.Text;

namespace ArniLite.Core
{
    public static class ImageAsset
    {
        public const int MaxPromptLength = 10000;
        public const int MaxBytes = 3000000;
        public static readonly string[] AspectRatios = { "16:9", "1:1", "21:9", "2:3", "3:2", "4:5", "5:4", "9:16", "9:21" };

        public static bool TryTarget(string root, string relative, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            var path = (relative ?? string.Empty).Trim();
            if (path.Length == 0 || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                error = "Choose a relative PNG path inside the project.";
                return false;
            }

            if (!WorkspacePath.TryCombine(root, path, out fullPath, out error))
            {
                return false;
            }

            return true;
        }

        public static string NormalizeRatio(string ratio)
        {
            var value = string.IsNullOrWhiteSpace(ratio) ? "16:9" : ratio.Trim();
            for (var i = 0; i < AspectRatios.Length; i++)
            {
                if (string.Equals(AspectRatios[i], value, StringComparison.Ordinal))
                {
                    return AspectRatios[i];
                }
            }

            return null;
        }

        public static string RequestKey(string root, string relative, string prompt, string ratio)
        {
            var text = (root ?? string.Empty) + "\n" + (relative ?? string.Empty).Trim() + "\n" + (prompt ?? string.Empty).Trim() + "\n" + ratio;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var builder = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        public static bool HasUnsupportedScript(string prompt)
        {
            var text = prompt ?? string.Empty;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] >= '\u0400' && text[i] <= '\u04FF')
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsPng(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 24 || bytes.Length > MaxBytes)
            {
                return false;
            }

            return bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
        }
    }
}
