using System;
using System.IO;

namespace ArniLite.Core
{
    public static class AgentRules
    {
        public const int MaxChars = 6000;

        public static bool Exists(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            try
            {
                return File.Exists(Path.Combine(root, "AGENTS.md"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        public static string Read(string root)
        {
            if (!Exists(root))
            {
                return string.Empty;
            }

            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return string.Empty;
            }

            text = text.Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            if (text.Length > MaxChars)
            {
                text = text.Substring(0, MaxChars);
            }

            return "AGENTS.md:\n" + text + "\n";
        }
    }
}
