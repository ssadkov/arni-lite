using System;
using System.Collections.Generic;
using System.Text;
using ArniLite.Core;
using Newtonsoft.Json.Linq;

namespace ArniLite.Api
{
    public sealed class ToolCallAssembler
    {
        private readonly SortedDictionary<int, MutableCall> _calls = new SortedDictionary<int, MutableCall>();

        public void Add(int index, string id, string name, string arguments)
        {
            if (!_calls.TryGetValue(index, out var call))
            {
                call = new MutableCall { Index = index };
                _calls[index] = call;
            }

            if (!string.IsNullOrEmpty(id))
            {
                call.Id = id;
            }

            if (!string.IsNullOrEmpty(name))
            {
                call.Name = name;
            }

            if (!string.IsNullOrEmpty(arguments))
            {
                call.Arguments.Append(arguments);
            }
        }

        public bool TryComplete(out List<ToolCallRecord> calls, out string error)
        {
            calls = new List<ToolCallRecord>();
            error = string.Empty;
            foreach (var pair in _calls)
            {
                var call = pair.Value;
                var json = call.Arguments.ToString();
                if (string.IsNullOrEmpty(call.Id) || string.IsNullOrEmpty(call.Name))
                {
                    error = "A tool call is missing its id or name.";
                    calls.Clear();
                    return false;
                }

                if (json.Length == 0)
                {
                    error = "A tool call ended before its arguments arrived.";
                    calls.Clear();
                    return false;
                }

                try
                {
                    var parsed = JToken.Parse(json);
                    if (parsed.Type != JTokenType.Object)
                    {
                        error = "Tool arguments must be a JSON object.";
                        calls.Clear();
                        return false;
                    }
                }
                catch (Exception)
                {
                    error = "Tool arguments are not complete JSON.";
                    calls.Clear();
                    return false;
                }

                calls.Add(new ToolCallRecord
                {
                    Index = call.Index,
                    Id = call.Id,
                    Name = call.Name,
                    ArgumentsJson = json,
                });
            }

            return true;
        }

        private sealed class MutableCall
        {
            public int Index { get; set; }
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public StringBuilder Arguments { get; } = new StringBuilder();
        }
    }

    public static class ToolSchemas
    {
        public static JArray AgentTools()
        {
            return new JArray
            {
                Function("list_files", "List a bounded set of files under the selected project.", new JObject
                {
                    ["path"] = StringProp("Relative directory. Empty means the project root."),
                    ["depth"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 4 },
                    ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 200 },
                }, new JArray()),
                Function("read_file", "Read a bounded text range in the selected project.", new JObject
                {
                    ["path"] = StringProp("Relative file path."),
                    ["start_line"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                    ["max_lines"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500 },
                }, new JArray("path")),
                Function("search_text", "Literal text search inside the selected project.", new JObject
                {
                    ["query"] = StringProp("Literal text to find."),
                    ["path"] = StringProp("Optional relative directory."),
                    ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50 },
                }, new JArray("query")),
                Function("propose_file_change", "Prepare a text file change inside the project folder. Nothing is written until the user confirms. For a new file set must_not_exist to true.", new JObject
                {
                    ["path"] = StringProp("Relative file path."),
                    ["expected_hash"] = StringProp("SHA-256 of the current file. Omit only together with must_not_exist."),
                    ["must_not_exist"] = new JObject { ["type"] = "boolean" },
                    ["content"] = StringProp("Full new text of the file."),
                }, new JArray("path", "content")),
                Function("run_command", "Run one non-interactive command after the user confirms it. Pass an exact user-supplied command unchanged. The project folder is not a sandbox. Set timeout_ms to at least 300000 when hashing many files.", new JObject
                {
                    ["shell"] = new JObject { ["type"] = "string", ["enum"] = new JArray("powershell", "cmd") },
                    ["command"] = StringProp("The command text."),
                    ["cwd"] = StringProp("Relative working directory inside the project."),
                    ["timeout_ms"] = new JObject { ["type"] = "integer", ["minimum"] = 1000, ["maximum"] = 600000 },
                }, new JArray("shell", "command")),
            };
        }

        private static JObject Function(string name, string description, JObject properties, JArray required)
        {
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required,
                        ["additionalProperties"] = false,
                    },
                },
            };
        }

        private static JObject StringProp(string description)
        {
            return new JObject { ["type"] = "string", ["description"] = description };
        }
    }
}
