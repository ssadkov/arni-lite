using System;
using System.IO;

namespace ArniLite.Core
{
    public static class WorkspacePath
    {
        public static bool TryCombine(string root, string relative, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(root))
            {
                error = "Choose a project folder first.";
                return false;
            }

            relative = (relative ?? string.Empty).Replace('/', '\\').Trim();
            if (relative.IndexOf(':') >= 0 || relative.IndexOf('|') >= 0)
            {
                error = "Drive paths and alternate data streams are rejected.";
                return false;
            }

            if (relative.StartsWith("\\\\", StringComparison.Ordinal) || relative.StartsWith("//", StringComparison.Ordinal))
            {
                error = "UNC paths are rejected.";
                return false;
            }

            string rootFull;
            try
            {
                rootFull = Path.GetFullPath(root);
            }
            catch (Exception)
            {
                error = "The project folder path is invalid.";
                return false;
            }

            if (rootFull.StartsWith("\\\\", StringComparison.Ordinal))
            {
                error = "UNC projects are rejected.";
                return false;
            }

            if (Path.IsPathRooted(relative))
            {
                error = "Absolute paths are rejected.";
                return false;
            }

            string combined;
            try
            {
                combined = Path.GetFullPath(Path.Combine(rootFull, relative));
            }
            catch (Exception)
            {
                error = "The path is invalid.";
                return false;
            }

            var rootPrefix = rootFull.TrimEnd('\\') + "\\";
            if (!combined.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
                && !combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "The path leaves the project folder.";
                return false;
            }

            var name = Path.GetFileName(combined.TrimEnd('\\'));
            if (IsReserved(name))
            {
                error = "Reserved Windows device names are rejected.";
                return false;
            }

            fullPath = combined;
            return true;
        }

        public static bool IsReserved(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            var stem = fileName;
            var dot = stem.IndexOf('.');
            if (dot >= 0)
            {
                stem = stem.Substring(0, dot);
            }

            stem = stem.ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL")
            {
                return true;
            }

            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
            {
                return stem[3] >= '1' && stem[3] <= '9';
            }

            return false;
        }
    }

    public static class SecretPaths
    {
        public static bool IsExcluded(string relativePath)
        {
            var normalized = (relativePath ?? string.Empty).Replace('/', '\\');
            var parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || part.Equals(".vs", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("dist", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("packages", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var name = parts.Length == 0 ? string.Empty : parts[parts.Length - 1];
            if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var extension = Path.GetExtension(name);
            return extension.Equals(".pem", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".key", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase);
        }
    }
}
