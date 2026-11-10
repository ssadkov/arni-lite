using System;
using System.Collections.Generic;
using System.Text;

namespace ArniLite.Core
{
    public enum MarkdownKind
    {
        Paragraph,
        Heading,
        Table,
        Rule,
        Blank,
    }

    public sealed class MarkdownRun
    {
        public MarkdownRun(string text, bool bold = false, bool code = false)
        {
            Text = text ?? string.Empty;
            Bold = bold;
            Code = code;
        }

        public string Text { get; }
        public bool Bold { get; }
        public bool Code { get; }
        public bool Break { get; private set; }

        public static MarkdownRun LineBreak()
        {
            return new MarkdownRun(string.Empty) { Break = true };
        }
    }

    public sealed class MarkdownBlock
    {
        public MarkdownKind Kind { get; set; }
        public int HeadingLevel { get; set; }
        public List<MarkdownRun> Runs { get; set; } = new List<MarkdownRun>();

        public string Plain
        {
            get
            {
                var builder = new StringBuilder();
                for (var i = 0; i < Runs.Count; i++)
                {
                    if (Runs[i].Break)
                    {
                        builder.Append('\n');
                    }
                    else
                    {
                        builder.Append(Runs[i].Text);
                    }
                }

                return builder.ToString();
            }
        }
    }

    public static class PlainMarkdown
    {
        public static IList<MarkdownBlock> Format(string text)
        {
            var blocks = new List<MarkdownBlock>();
            var lines = SplitLines(text);
            var index = 0;
            while (index < lines.Count)
            {
                if (string.IsNullOrWhiteSpace(lines[index]))
                {
                    if (blocks.Count == 0 || blocks[blocks.Count - 1].Kind != MarkdownKind.Blank)
                    {
                        blocks.Add(new MarkdownBlock { Kind = MarkdownKind.Blank });
                    }

                    index++;
                    continue;
                }

                if (IsTableLine(lines[index]) && index + 1 < lines.Count && IsTableLine(lines[index + 1]))
                {
                    var rows = new List<string>();
                    while (index < lines.Count && IsTableLine(lines[index]))
                    {
                        rows.Add(lines[index]);
                        index++;
                    }

                    blocks.Add(BuildTable(rows));
                    continue;
                }

                if (IsRule(lines[index]))
                {
                    blocks.Add(new MarkdownBlock { Kind = MarkdownKind.Rule });
                    index++;
                    continue;
                }

                if (TryHeading(lines[index], out var level, out var heading))
                {
                    blocks.Add(new MarkdownBlock
                    {
                        Kind = MarkdownKind.Heading,
                        HeadingLevel = level,
                        Runs = ParseInline(heading),
                    });
                    index++;
                    continue;
                }

                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownKind.Paragraph,
                    Runs = ParseInline(lines[index]),
                });
                index++;
            }

            return blocks;
        }

        private static MarkdownBlock BuildTable(List<string> lines)
        {
            var parsed = new List<List<string>>();
            var separator = new bool[lines.Count];
            var columns = 0;
            for (var row = 0; row < lines.Count; row++)
            {
                var cells = Cells(lines[row]);
                parsed.Add(cells);
                if (cells.Count > columns)
                {
                    columns = cells.Count;
                }

                separator[row] = IsSeparator(cells);
            }

            var widths = new int[columns];
            for (var row = 0; row < parsed.Count; row++)
            {
                if (separator[row])
                {
                    continue;
                }

                for (var column = 0; column < columns; column++)
                {
                    var cell = column < parsed[row].Count ? parsed[row][column] : string.Empty;
                    var visible = VisibleLength(cell);
                    if (visible > widths[column])
                    {
                        widths[column] = visible;
                    }
                }
            }

            for (var column = 0; column < columns; column++)
            {
                if (widths[column] < 1)
                {
                    widths[column] = 1;
                }
            }

            var header = -1;
            for (var row = 0; row < parsed.Count; row++)
            {
                if (!separator[row])
                {
                    header = row;
                    break;
                }
            }

            var runs = new List<MarkdownRun>();
            for (var row = 0; row < parsed.Count; row++)
            {
                if (row > 0)
                {
                    runs.Add(MarkdownRun.LineBreak());
                }

                if (separator[row])
                {
                    for (var column = 0; column < columns; column++)
                    {
                        if (column > 0)
                        {
                            runs.Add(new MarkdownRun("  "));
                        }

                        runs.Add(new MarkdownRun(new string('-', widths[column])));
                    }

                    continue;
                }

                for (var column = 0; column < columns; column++)
                {
                    if (column > 0)
                    {
                        runs.Add(new MarkdownRun("  "));
                    }

                    var cell = column < parsed[row].Count ? parsed[row][column] : string.Empty;
                    var visible = 0;
                    var pieces = ParseInline(cell);
                    for (var piece = 0; piece < pieces.Count; piece++)
                    {
                        if (pieces[piece].Text.Length == 0)
                        {
                            continue;
                        }

                        visible += pieces[piece].Text.Length;
                        runs.Add(new MarkdownRun(pieces[piece].Text, row == header || pieces[piece].Bold, pieces[piece].Code));
                    }

                    var pad = widths[column] - visible;
                    if (pad > 0 && column < columns - 1)
                    {
                        runs.Add(new MarkdownRun(new string(' ', pad)));
                    }
                }
            }

            return new MarkdownBlock { Kind = MarkdownKind.Table, Runs = runs };
        }

        private static List<MarkdownRun> ParseInline(string text)
        {
            var runs = new List<MarkdownRun>();
            var source = text ?? string.Empty;
            var index = 0;
            var plainStart = 0;
            while (index < source.Length)
            {
                if (TrySpan(source, index, out var markerLength, out var close, out var bold, out var code))
                {
                    if (index > plainStart)
                    {
                        runs.Add(new MarkdownRun(source.Substring(plainStart, index - plainStart)));
                    }

                    runs.Add(new MarkdownRun(source.Substring(index + markerLength, close - index - markerLength), bold, code));
                    index = close + markerLength;
                    plainStart = index;
                    continue;
                }

                index++;
            }

            if (plainStart < source.Length)
            {
                runs.Add(new MarkdownRun(source.Substring(plainStart)));
            }

            if (runs.Count == 0)
            {
                runs.Add(new MarkdownRun(string.Empty));
            }

            return runs;
        }

        private static bool TrySpan(string text, int index, out int markerLength, out int close, out bool bold, out bool code)
        {
            markerLength = 0;
            close = -1;
            bold = false;
            code = false;
            if (text[index] == '`')
            {
                var end = text.IndexOf('`', index + 1);
                if (end > index + 1)
                {
                    markerLength = 1;
                    close = end;
                    code = true;
                    return true;
                }
            }

            if (HasMarker(text, index, "**"))
            {
                var end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                if (end > index + 2)
                {
                    markerLength = 2;
                    close = end;
                    bold = true;
                    return true;
                }
            }

            if (HasMarker(text, index, "__"))
            {
                var end = text.IndexOf("__", index + 2, StringComparison.Ordinal);
                if (end > index + 2)
                {
                    markerLength = 2;
                    close = end;
                    bold = true;
                    return true;
                }
            }

            return false;
        }

        private static bool HasMarker(string text, int index, string marker)
        {
            return index + marker.Length <= text.Length && text.Substring(index, marker.Length) == marker;
        }

        private static int VisibleLength(string cell)
        {
            var pieces = ParseInline(cell);
            var length = 0;
            for (var i = 0; i < pieces.Count; i++)
            {
                length += pieces[i].Text.Length;
            }

            return length;
        }

        private static List<string> Cells(string line)
        {
            var trimmed = (line ?? string.Empty).Trim();
            if (trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1);
            }

            if (trimmed.EndsWith("|", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            var parts = trimmed.Split('|');
            var cells = new List<string>();
            for (var i = 0; i < parts.Length; i++)
            {
                cells.Add(parts[i].Trim());
            }

            return cells;
        }

        private static bool IsSeparator(List<string> cells)
        {
            if (cells.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < cells.Count; i++)
            {
                if (!IsDashCell(cells[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsDashCell(string cell)
        {
            if (string.IsNullOrEmpty(cell) || cell.Length < 3)
            {
                return false;
            }

            var start = cell[0] == ':' ? 1 : 0;
            var end = cell[cell.Length - 1] == ':' ? cell.Length - 1 : cell.Length;
            if (end - start < 3)
            {
                return false;
            }

            for (var i = start; i < end; i++)
            {
                if (cell[i] != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsTableLine(string line)
        {
            var trimmed = (line ?? string.Empty).Trim();
            var pipes = 0;
            for (var i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '|')
                {
                    pipes++;
                }
            }

            return pipes >= 2;
        }

        private static bool IsRule(string line)
        {
            var trimmed = (line ?? string.Empty).Trim();
            if (trimmed.Length < 3)
            {
                return false;
            }

            var mark = trimmed[0];
            if (mark != '-' && mark != '*' && mark != '_')
            {
                return false;
            }

            var marks = 0;
            for (var i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == mark)
                {
                    marks++;
                    continue;
                }

                if (trimmed[i] != ' ')
                {
                    return false;
                }
            }

            return marks >= 3;
        }

        private static bool TryHeading(string line, out int level, out string rest)
        {
            var trimmed = (line ?? string.Empty).Trim();
            level = 0;
            rest = trimmed;
            while (level < trimmed.Length && level < 6 && trimmed[level] == '#')
            {
                level++;
            }

            if (level == 0 || level >= trimmed.Length || trimmed[level] != ' ')
            {
                level = 0;
                return false;
            }

            rest = trimmed.Substring(level + 1).Trim();
            return true;
        }

        private static List<string> SplitLines(string text)
        {
            var source = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            return new List<string>(source.Split('\n'));
        }
    }
}
