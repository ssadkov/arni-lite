using System;
using System.Collections.Generic;
using System.Text;

namespace ArniLite.Api
{
    public sealed class SseEvent
    {
        public SseEvent(string eventName, string data)
        {
            EventName = string.IsNullOrEmpty(eventName) ? "message" : eventName;
            Data = data ?? string.Empty;
        }

        public string EventName { get; }
        public string Data { get; }
    }

    /// <summary>
    /// Incremental UTF-8 SSE parser. Events are emitted only on a blank-line delimiter.
    /// An unfinished tail stays in <see cref="Remainder"/> and is not guessed into an event.
    /// </summary>
    public sealed class SseReader
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _pending = new StringBuilder();

        public string Remainder => _pending.ToString();

        public IList<SseEvent> Push(byte[] buffer, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (count < 0 || count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (count == 0)
            {
                return new List<SseEvent>();
            }

            var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
            var produced = _decoder.GetChars(buffer, 0, count, chars, 0, false);
            _pending.Append(chars, 0, produced);
            return Drain();
        }

        private IList<SseEvent> Drain()
        {
            var events = new List<SseEvent>();
            while (true)
            {
                var text = _pending.ToString();
                var separator = FindSeparator(text, out var separatorLength);
                if (separator < 0)
                {
                    break;
                }

                var block = text.Substring(0, separator);
                _pending.Remove(0, separator + separatorLength);
                var parsed = ParseBlock(block);
                if (parsed != null)
                {
                    events.Add(parsed);
                }
            }

            return events;
        }

        private static int FindSeparator(string text, out int length)
        {
            var best = -1;
            length = 0;
            Consider(text, "\r\n\r\n", 4, ref best, ref length);
            Consider(text, "\n\n", 2, ref best, ref length);
            Consider(text, "\r\r", 2, ref best, ref length);
            return best;
        }

        private static void Consider(string text, string separator, int separatorLength, ref int best, ref int length)
        {
            var index = text.IndexOf(separator, StringComparison.Ordinal);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
                length = separatorLength;
            }
        }

        private static SseEvent? ParseBlock(string block)
        {
            var eventName = "message";
            var data = new StringBuilder();
            var anyData = false;
            foreach (var line in SplitLines(block))
            {
                if (line.Length == 0 || line[0] == ':')
                {
                    continue;
                }

                string field;
                string value;
                var colon = line.IndexOf(':');
                if (colon < 0)
                {
                    field = line;
                    value = string.Empty;
                }
                else
                {
                    field = line.Substring(0, colon);
                    value = line.Substring(colon + 1);
                    if (value.StartsWith(" ", StringComparison.Ordinal))
                    {
                        value = value.Substring(1);
                    }
                }

                if (field == "event")
                {
                    eventName = value;
                }
                else if (field == "data")
                {
                    if (anyData)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    anyData = true;
                }
            }

            if (!anyData)
            {
                return null;
            }

            return new SseEvent(eventName, data.ToString());
        }

        private static IList<string> SplitLines(string block)
        {
            var lines = new List<string>();
            var start = 0;
            for (var i = 0; i < block.Length; i++)
            {
                if (block[i] != '\n' && block[i] != '\r')
                {
                    continue;
                }

                lines.Add(block.Substring(start, i - start));
                if (block[i] == '\r' && i + 1 < block.Length && block[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }

            if (start < block.Length)
            {
                lines.Add(block.Substring(start));
            }

            return lines;
        }
    }
}
