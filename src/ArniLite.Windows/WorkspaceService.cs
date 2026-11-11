using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ArniLite.Core;

namespace ArniLite.Windows
{
    public sealed class WorkspaceService
    {
        public const int MaxReadLines = 500;
        public const int MaxReadChars = 64 * 1024;
        public string Root { get; private set; } = string.Empty;

        public void ClearRoot()
        {
            Root = string.Empty;
        }

        public void SetRoot(string path)
        {
            if (!Directory.Exists(path))
            {
                throw new IOException("The project folder does not exist.");
            }

            if (!WorkspacePath.TryCombine(path, string.Empty, out var full, out var error))
            {
                throw new IOException(error);
            }

            if (HasReparsePoint(full, full))
            {
                throw new IOException("Reparse points are rejected for the project root.");
            }

            Root = full;
        }

        public IList<string> List(string relative, int depth, int limit)
        {
            if (!WorkspacePath.TryCombine(Root, relative, out var full, out var error))
            {
                throw new IOException(error);
            }

            EnsureNoReparse(full);
            var results = new List<string>();
            Walk(full, depth < 0 ? 1 : depth, limit < 1 ? 50 : Math.Min(limit, 200), results);
            return results;
        }

        public FileSlice Read(string relative, int startLine, int maxLines)
        {
            if (!WorkspacePath.TryCombine(Root, relative, out var full, out var error))
            {
                throw new IOException(error);
            }

            EnsureNoReparse(full);
            if (!File.Exists(full))
            {
                throw new IOException("File not found.");
            }

            var bytes = File.ReadAllBytes(full);
            if (!TryDecode(bytes, out var text, out var encoding))
            {
                throw new IOException("The file encoding is ambiguous. It was not guessed.");
            }

            var lines = SplitLines(text);
            var start = Math.Max(1, startLine);
            var count = maxLines < 1 ? MaxReadLines : Math.Min(maxLines, MaxReadLines);
            var slice = new StringBuilder();
            var emitted = 0;
            for (var i = start - 1; i < lines.Count && emitted < count; i++)
            {
                if (slice.Length > 0)
                {
                    slice.Append('\n');
                }

                if (slice.Length + lines[i].Length > MaxReadChars)
                {
                    break;
                }

                slice.Append(lines[i]);
                emitted++;
            }

            return new FileSlice
            {
                Path = Relative(full),
                Text = slice.ToString(),
                Encoding = encoding,
                Newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "crlf" : "lf",
                Hash = Hash(bytes),
                StartLine = start,
                LineCount = lines.Count,
                Truncated = start - 1 + emitted < lines.Count,
            };
        }

        public IList<string> Search(string query, string relative, int limit)
        {
            if (string.IsNullOrEmpty(query))
            {
                throw new IOException("The search query is empty.");
            }

            var matches = new List<string>();
            var cap = limit < 1 ? 20 : Math.Min(limit, 50);
            foreach (var file in List(relative, 4, 200))
            {
                if (matches.Count >= cap || SecretPaths.IsExcluded(file))
                {
                    continue;
                }

                FileSlice slice;
                try
                {
                    slice = Read(file, 1, MaxReadLines);
                }
                catch (IOException)
                {
                    continue;
                }

                var lines = SplitLines(slice.Text);
                for (var i = 0; i < lines.Count && matches.Count < cap; i++)
                {
                    if (lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matches.Add(file + ":" + (i + 1) + ": " + Trim(lines[i], 180));
                    }
                }
            }

            return matches;
        }

        public ChangeProposal Propose(string relative, string expectedHash, bool mustNotExist, string content)
        {
            if (!WorkspacePath.TryCombine(Root, relative, out var full, out var error))
            {
                throw new IOException(error);
            }

            EnsureNoReparse(full);
            var exists = File.Exists(full);
            if (mustNotExist && exists)
            {
                throw new IOException("The file already exists.");
            }

            if (!mustNotExist && !exists && string.IsNullOrEmpty(expectedHash))
            {
                throw new IOException("expected_hash or must_not_exist is required.");
            }

            string current = string.Empty;
            string hash = string.Empty;
            string encoding = "utf-8";
            string newline = "crlf";
            if (exists)
            {
                var slice = Read(relative, 1, MaxReadLines);
                current = File.ReadAllText(full);
                hash = slice.Hash;
                encoding = slice.Encoding;
                newline = slice.Newline;
                if (!string.IsNullOrEmpty(expectedHash) && !string.Equals(expectedHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The file hash does not match expected_hash.");
                }
            }
            else if (!mustNotExist && !string.IsNullOrEmpty(expectedHash))
            {
                throw new IOException("The file does not exist.");
            }

            return new ChangeProposal
            {
                FullPath = full,
                RelativePath = Relative(full),
                ExpectedHash = hash,
                MustNotExist = !exists,
                Encoding = encoding,
                Newline = newline,
                Before = current,
                After = content ?? string.Empty,
                Preview = Preview(current, content ?? string.Empty),
            };
        }

        public string Apply(ChangeProposal proposal)
        {
            if (proposal == null)
            {
                throw new ArgumentNullException(nameof(proposal));
            }

            EnsureNoReparse(proposal.FullPath);
            var exists = File.Exists(proposal.FullPath);
            if (proposal.MustNotExist && exists)
            {
                throw new IOException("The file appeared after the preview.");
            }

            if (!proposal.MustNotExist)
            {
                var hash = Hash(File.ReadAllBytes(proposal.FullPath));
                if (!string.Equals(hash, proposal.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The file changed after the preview.");
                }
            }

            var text = proposal.Newline == "crlf"
                ? (proposal.After ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n")
                : (proposal.After ?? string.Empty).Replace("\r\n", "\n");
            var encoding = proposal.Encoding == "utf-8-bom" ? new UTF8Encoding(true) : new UTF8Encoding(false);
            var bytes = encoding.GetBytes(text);
            var temporary = proposal.FullPath + ".arnilite.tmp";
            File.WriteAllBytes(temporary, bytes);
            if (!exists)
            {
                File.Move(temporary, proposal.FullPath);
                return Hash(bytes);
            }

            var backup = proposal.FullPath + ".arnilite.bak";
            File.Replace(temporary, proposal.FullPath, backup, ignoreMetadataErrors: true);
            return Hash(File.ReadAllBytes(proposal.FullPath));
        }

        private void Walk(string directory, int depth, int limit, List<string> results)
        {
            if (results.Count >= limit || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var path in Directory.GetFileSystemEntries(directory))
            {
                if (results.Count >= limit)
                {
                    return;
                }

                var relative = Relative(path);
                if (SecretPaths.IsExcluded(relative) || HasReparsePoint(Root, path))
                {
                    continue;
                }

                results.Add(relative);
                if (depth > 0 && Directory.Exists(path))
                {
                    Walk(path, depth - 1, limit, results);
                }
            }
        }

        private string Relative(string full)
        {
            var prefix = Root.TrimEnd('\\') + "\\";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return full.Substring(prefix.Length);
            }

            return full;
        }

        private void EnsureNoReparse(string full)
        {
            if (HasReparsePoint(Root, full))
            {
                throw new IOException("Reparse points are rejected.");
            }
        }

        private static bool HasReparsePoint(string root, string full)
        {
            var current = root;
            if (IsReparse(current))
            {
                return true;
            }

            var remainder = full.Substring(Math.Min(full.Length, root.TrimEnd('\\').Length)).TrimStart('\\');
            var parts = remainder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                if (Directory.Exists(current) || File.Exists(current))
                {
                    if (IsReparse(current))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsReparse(string path)
        {
            var attributes = GetFileAttributes(path);
            return attributes != -1 && (attributes & 0x400) != 0;
        }

        private static bool TryDecode(byte[] bytes, out string text, out string encoding)
        {
            text = string.Empty;
            encoding = string.Empty;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                encoding = "utf-8-bom";
                return true;
            }

            if (IsUtf8(bytes))
            {
                text = Encoding.UTF8.GetString(bytes);
                encoding = "utf-8";
                return true;
            }

            return false;
        }

        private static bool IsUtf8(byte[] bytes)
        {
            var decoder = Encoding.UTF8.GetDecoder();
            try
            {
                decoder.Fallback = DecoderFallback.ExceptionFallback;
                var chars = new char[decoder.GetCharCount(bytes, 0, bytes.Length, flush: true)];
                decoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: true);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static List<string> SplitLines(string text)
        {
            return new List<string>((text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string Preview(string before, string after)
        {
            var oldLines = SplitLines(before);
            var newLines = SplitLines(after);
            var prefix = 0;
            while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix])
            {
                prefix++;
            }

            var oldSuffix = oldLines.Count;
            var newSuffix = newLines.Count;
            while (oldSuffix > prefix && newSuffix > prefix && oldLines[oldSuffix - 1] == newLines[newSuffix - 1])
            {
                oldSuffix--;
                newSuffix--;
            }

            var builder = new StringBuilder();
            for (var i = prefix; i < oldSuffix && builder.Length < 4000; i++)
            {
                builder.Append("- ").AppendLine(Trim(oldLines[i], 200));
            }

            for (var i = prefix; i < newSuffix && builder.Length < 8000; i++)
            {
                builder.Append("+ ").AppendLine(Trim(newLines[i], 200));
            }

            return builder.Length == 0 ? "No text changes." : builder.ToString();
        }

        private static string Trim(string value, int max)
        {
            if (value.Length <= max)
            {
                return value;
            }

            return value.Substring(0, max) + "...";
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetFileAttributes(string path);
    }

    public sealed class FileSlice
    {
        public string Path { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Encoding { get; set; } = string.Empty;
        public string Newline { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int LineCount { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class ChangeProposal
    {
        public string FullPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string ExpectedHash { get; set; } = string.Empty;
        public bool MustNotExist { get; set; }
        public string Encoding { get; set; } = "utf-8";
        public string Newline { get; set; } = "crlf";
        public string Before { get; set; } = string.Empty;
        public string After { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty;
    }
}
