using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Core;
using Newtonsoft.Json.Linq;

namespace ArniLite.Api
{
    public sealed class McpBundle
    {
        private const int MaxTools = 48;

        private sealed class BoundTool
        {
            public McpSession Session;
            public string Host;
            public string OriginalName;
            public string ExposedName;
            public McpTool Tool;
        }

        private readonly List<McpSession> _sessions = new List<McpSession>();
        private readonly List<BoundTool> _tools = new List<BoundTool>();

        public static McpBundle Combine(IList<McpSession> sessions)
        {
            var bundle = new McpBundle();
            var used = new HashSet<string>(StringComparer.Ordinal);
            if (sessions == null)
            {
                return bundle;
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session == null)
                {
                    continue;
                }

                bundle._sessions.Add(session);
                var host = McpAddress.Host(session.Url);
                var label = Label(host);
                for (var t = 0; t < session.Tools.Count && bundle._tools.Count < MaxTools; t++)
                {
                    var tool = session.Tools[t];
                    var exposed = tool.Name;
                    if (used.Contains(exposed))
                    {
                        exposed = label + "_" + tool.Name;
                        if (exposed.Length > 64)
                        {
                            exposed = exposed.Substring(0, 64);
                        }

                        if (used.Contains(exposed))
                        {
                            continue;
                        }
                    }

                    used.Add(exposed);
                    bundle._tools.Add(new BoundTool
                    {
                        Session = session,
                        Host = host,
                        OriginalName = tool.Name,
                        ExposedName = exposed,
                        Tool = tool,
                    });
                }
            }

            return bundle;
        }

        public bool Owns(string name)
        {
            return Find(name) != null;
        }

        public Task<McpCallResult> CallAsync(string name, string argumentsJson, CancellationToken cancellationToken)
        {
            var bound = Find(name);
            if (bound == null)
            {
                throw new InvalidOperationException("The MCP server does not provide this tool.");
            }

            return bound.Session.CallAsync(bound.OriginalName, argumentsJson, cancellationToken);
        }

        public JArray ToOpenAiTools()
        {
            var array = new JArray();
            for (var i = 0; i < _tools.Count; i++)
            {
                var tool = _tools[i];
                array.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool.ExposedName,
                        ["description"] = tool.Tool.Description,
                        ["parameters"] = tool.Tool.Parameters.DeepClone(),
                    },
                });
            }

            return array;
        }

        public string PromptLine()
        {
            if (_tools.Count == 0)
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            text.Append("Удалённые MCP уже подключены. Функции из списка ниже уже есть среди инструментов. Не пиши, что MCP-клиента нет. Не открывай адрес сервера через web_search, curl или powershell. Вызывай эти функции напрямую.\n");
            string host = null;
            for (var i = 0; i < _tools.Count; i++)
            {
                var tool = _tools[i];
                if (!string.Equals(host, tool.Host, StringComparison.Ordinal))
                {
                    host = tool.Host;
                    text.Append("Сервер ").Append(host).Append(":\n");
                    var instructions = tool.Session.Instructions ?? string.Empty;
                    if (instructions.Length > 0)
                    {
                        text.Append("Инструкция сервера:\n").Append(instructions.Trim()).Append('\n');
                    }
                }

                text.Append("- ").Append(tool.ExposedName).Append(": ").Append(tool.Tool.Description).Append('\n');
            }

            return text.ToString();
        }

        public string Describe()
        {
            var text = new StringBuilder();
            for (var s = 0; s < _sessions.Count; s++)
            {
                if (text.Length > 0)
                {
                    text.AppendLine();
                }

                var host = McpAddress.Host(_sessions[s].Url);
                text.Append(host);
                for (var i = 0; i < _tools.Count; i++)
                {
                    if (ReferenceEquals(_tools[i].Session, _sessions[s]))
                    {
                        text.AppendLine().Append(_tools[i].ExposedName);
                    }
                }
            }

            return text.ToString();
        }

        public async Task CloseAsync()
        {
            for (var i = 0; i < _sessions.Count; i++)
            {
                await _sessions[i].CloseAsync().ConfigureAwait(false);
            }
        }

        private BoundTool Find(string name)
        {
            for (var i = 0; i < _tools.Count; i++)
            {
                if (string.Equals(_tools[i].ExposedName, name, StringComparison.Ordinal))
                {
                    return _tools[i];
                }
            }

            return null;
        }

        private static string Label(string host)
        {
            var text = new StringBuilder();
            var source = (host ?? string.Empty).ToLowerInvariant();
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    text.Append(ch);
                }
                else if (text.Length > 0 && text[text.Length - 1] != '_')
                {
                    text.Append('_');
                }
            }

            var label = text.ToString().Trim('_');
            if (label.Length > 24)
            {
                label = label.Substring(0, 24).Trim('_');
            }

            return label.Length == 0 ? "mcp" : label;
        }
    }
}
