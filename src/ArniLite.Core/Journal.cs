using System;
using System.Collections.Generic;

namespace ArniLite.Core
{
    public sealed class SideEffectEntry
    {
        public string Key { get; set; } = string.Empty;
        public string ArgumentsHash { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string ResultJson { get; set; } = string.Empty;
    }

    public interface ISideEffectJournal
    {
        SideEffectEntry Latest(string key);
        void Append(SideEffectEntry entry);
        void MarkExecutingAsUnknown();
    }

    public static class SideEffectGuard
    {
        public static bool TryPrepare(ISideEffectJournal journal, string key, string argumentsHash, out string blockedStatus, out string blockedJson)
        {
            blockedStatus = string.Empty;
            blockedJson = string.Empty;
            var latest = journal.Latest(key);
            if (latest == null)
            {
                journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = argumentsHash, State = "prepared" });
                return true;
            }

            if (!string.Equals(latest.ArgumentsHash, argumentsHash, StringComparison.Ordinal))
            {
                blockedStatus = "failed";
                blockedJson = Result("failed", "This tool call id was already used with different arguments.");
                return false;
            }

            if (latest.State == "prepared" || latest.State == "executing")
            {
                blockedStatus = "outcome_unknown";
                blockedJson = Result("outcome_unknown", "The previous attempt was interrupted. It was not run again. Check the project before retrying.");
                journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = argumentsHash, State = "outcome_unknown", ResultJson = blockedJson });
                return false;
            }

            blockedStatus = latest.State;
            blockedJson = string.IsNullOrEmpty(latest.ResultJson)
                ? Result(latest.State, "Stored result from the journal.")
                : latest.ResultJson;
            return false;
        }

        public static string Result(string status, string message)
        {
            return "{\"schema_version\":1,\"status\":\"" + status + "\",\"result\":{\"message\":\"" + Escape(message) + "\"}}";
        }

        public static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    public static class CodeFences
    {
        public static IList<CodeFencePart> Split(string text)
        {
            var parts = new List<CodeFencePart>();
            var source = text ?? string.Empty;
            var cursor = 0;
            while (cursor < source.Length)
            {
                var open = source.IndexOf("```", cursor, StringComparison.Ordinal);
                if (open < 0)
                {
                    Add(parts, false, source.Substring(cursor));
                    break;
                }

                Add(parts, false, source.Substring(cursor, open - cursor));
                var lineEnd = source.IndexOf('\n', open);
                var contentStart = lineEnd < 0 ? source.Length : lineEnd + 1;
                var close = source.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (close < 0)
                {
                    Add(parts, true, source.Substring(contentStart));
                    break;
                }

                Add(parts, true, source.Substring(contentStart, close - contentStart).TrimEnd('\r', '\n'));
                cursor = close + 3;
            }

            if (parts.Count == 0)
            {
                parts.Add(new CodeFencePart(false, string.Empty));
            }

            return parts;
        }

        private static void Add(List<CodeFencePart> parts, bool code, string text)
        {
            if (text.Length == 0 && !code)
            {
                return;
            }

            parts.Add(new CodeFencePart(code, text));
        }
    }

    public sealed class CodeFencePart
    {
        public CodeFencePart(bool code, string text)
        {
            IsCode = code;
            Text = text ?? string.Empty;
        }

        public bool IsCode { get; }
        public string Text { get; }
    }
}
