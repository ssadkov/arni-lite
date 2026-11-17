using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ArniLite.Api
{
    public sealed class McpTool
    {
        public McpTool(string name, string description, JObject parameters)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
            Parameters = parameters ?? new JObject { ["type"] = "object" };
        }

        public string Name { get; }
        public string Description { get; }
        public JObject Parameters { get; }
    }

    public sealed class McpCallResult
    {
        public bool IsError { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public sealed class McpSession
    {
        private readonly HttpClient _http;
        private readonly string _url;
        private readonly string _authorization;
        private readonly List<McpTool> _tools;
        private string _sessionId;
        private string _instructions = string.Empty;
        private int _nextId = 1;
        private bool _closed;

        internal McpSession(HttpClient http, string url, string authorization, string sessionId, List<McpTool> tools, int nextId)
        {
            _http = http;
            _url = url;
            _authorization = authorization ?? string.Empty;
            _sessionId = sessionId ?? string.Empty;
            _tools = tools ?? new List<McpTool>();
            _nextId = nextId;
        }

        public IList<McpTool> Tools => _tools;
        public string Url => _url;
        public string Instructions => _instructions;

        internal void ReplaceTools(List<McpTool> tools)
        {
            _tools.Clear();
            if (tools == null)
            {
                return;
            }

            for (var i = 0; i < tools.Count && i < 32; i++)
            {
                _tools.Add(tools[i]);
            }
        }

        public bool Owns(string name)
        {
            for (var i = 0; i < _tools.Count; i++)
            {
                if (string.Equals(_tools[i].Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
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
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.Parameters.DeepClone(),
                    },
                });
            }

            return array;
        }

        internal void SetInstructions(string instructions)
        {
            _instructions = instructions ?? string.Empty;
            if (_instructions.Length > 1500)
            {
                _instructions = _instructions.Substring(0, 1500);
            }
        }

        public string PromptLine()
        {
            if (_tools.Count == 0)
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            text.Append("Удалённый MCP уже подключён. Функции из списка ниже уже есть среди инструментов. Не пиши, что MCP-клиента нет. Не открывай адрес сервера через web_search, curl или powershell. Вызывай эти функции напрямую.\n");
            if (_instructions.Length > 0)
            {
                text.Append("Инструкция сервера:\n").Append(_instructions.Trim()).Append('\n');
            }

            for (var i = 0; i < _tools.Count; i++)
            {
                text.Append("- ").Append(_tools[i].Name).Append(": ").Append(_tools[i].Description).Append('\n');
            }

            return text.ToString();
        }

        public async Task<McpCallResult> CallAsync(string name, string argumentsJson, CancellationToken cancellationToken)
        {
            if (!Owns(name))
            {
                throw new InvalidOperationException("The MCP server does not provide this tool.");
            }

            JObject arguments;
            try
            {
                arguments = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            }
            catch (Exception)
            {
                throw new InvalidOperationException("MCP tool arguments must be a JSON object.");
            }

            var result = await SendAsync("tools/call", new JObject
            {
                ["name"] = name,
                ["arguments"] = arguments,
            }, true, cancellationToken).ConfigureAwait(false);
            return ReadCallResult(result);
        }

        public async Task CloseAsync()
        {
            if (_closed || string.IsNullOrEmpty(_sessionId))
            {
                _closed = true;
                return;
            }

            _closed = true;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, _url);
                AddHeaders(request);
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        internal async Task<JObject> SendAsync(string method, JObject parameters, bool expectResult, CancellationToken cancellationToken)
        {
            var id = _nextId++;
            var payload = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
            };
            if (parameters != null)
            {
                payload["params"] = parameters;
            }

            if (expectResult)
            {
                payload["id"] = id;
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Post, _url);
            AddHeaders(request);
            request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("MCP timed out.");
            }

            using (response)
            {
                RememberSession(response);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Accepted)
                {
                    throw new InvalidOperationException("MCP returned HTTP " + (int)response.StatusCode + ".");
                }

                if (!expectResult || response.Content == null)
                {
                    return null;
                }

                var body = await ReadLimitedAsync(response.Content, 512 * 1024, limit.Token).ConfigureAwait(false);
                var message = SelectMessage(body, response.Content.Headers.ContentType?.MediaType ?? string.Empty, id);
                if (message == null)
                {
                    throw new InvalidOperationException("MCP returned no result.");
                }

                var error = message["error"] as JObject;
                if (error != null)
                {
                    var text = ((string)error["message"] ?? "MCP request failed.").Trim();
                    if (text.Length > 240)
                    {
                        text = text.Substring(0, 240);
                    }

                    throw new InvalidOperationException(text);
                }

                return message["result"] as JObject ?? new JObject();
            }
        }

        private void AddHeaders(HttpRequestMessage request)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-03-26");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (!string.IsNullOrEmpty(_authorization))
            {
                request.Headers.TryAddWithoutValidation("Authorization", _authorization);
            }

            if (!string.IsNullOrEmpty(_sessionId))
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            }
        }

        private void RememberSession(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _sessionId = value.Trim();
                        return;
                    }
                }
            }
        }

        internal static McpCallResult ReadCallResult(JObject result)
        {
            var text = new StringBuilder();
            var content = result?["content"] as JArray;
            if (content != null)
            {
                foreach (var item in content)
                {
                    if (!string.Equals((string)item["type"], "text", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var piece = (string)item["text"] ?? string.Empty;
                    if (piece.Length == 0)
                    {
                        continue;
                    }

                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(piece);
                }
            }

            var value = text.ToString().Trim();
            if (value.Length > 8000)
            {
                value = value.Substring(0, 8000);
            }

            if (value.Length == 0)
            {
                value = "The tool returned no text.";
            }

            return new McpCallResult
            {
                IsError = result != null && (bool?)result["isError"] == true,
                Text = value,
            };
        }

        private static async Task<string> ReadLimitedAsync(HttpContent content, int max, CancellationToken cancellationToken)
        {
            var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using (stream)
            {
                var buffer = new byte[8192];
                using var copy = new MemoryStream();
                while (copy.Length < max)
                {
                    var count = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, max - copy.Length), cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    copy.Write(buffer, 0, count);
                }

                if (copy.Length >= max)
                {
                    throw new InvalidOperationException("MCP response is too large.");
                }

                return Encoding.UTF8.GetString(copy.ToArray());
            }
        }

        private static JObject SelectMessage(string body, string mediaType, int id)
        {
            body = (body ?? string.Empty).Trim();
            if (body.Length == 0)
            {
                return null;
            }

            if (mediaType.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0 || body.StartsWith("data:", StringComparison.Ordinal) || body.StartsWith("event:", StringComparison.Ordinal))
            {
                var reader = new SseReader();
                var flushed = body.EndsWith("\n\n", StringComparison.Ordinal) ? body : body + "\n\n";
                var encoded = Encoding.UTF8.GetBytes(flushed);
                var events = reader.Push(encoded, encoded.Length);
                JObject matched = null;
                for (var i = 0; i < events.Count; i++)
                {
                    var message = ParseObject(events[i].Data);
                    if (message != null && IdMatches(message["id"], id))
                    {
                        matched = message;
                    }
                }

                return matched;
            }

            var parsed = ParseObject(body);
            if (parsed != null)
            {
                return IdMatches(parsed["id"], id) ? parsed : null;
            }

            var array = ParseArray(body);
            if (array == null)
            {
                return null;
            }

            foreach (var item in array)
            {
                var message = item as JObject;
                if (message != null && IdMatches(message["id"], id))
                {
                    return message;
                }
            }

            return null;
        }

        private static bool IdMatches(JToken id, int expected)
        {
            if (id == null)
            {
                return false;
            }

            if (id.Type == JTokenType.Integer)
            {
                return (int)id == expected;
            }

            return string.Equals(id.ToString(), expected.ToString(), StringComparison.Ordinal);
        }

        private static JObject ParseObject(string json)
        {
            try
            {
                var token = JToken.Parse(json);
                return token as JObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static JArray ParseArray(string json)
        {
            try
            {
                return JToken.Parse(json) as JArray;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public static class McpClient
    {
        private static readonly HashSet<string> OwnTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "list_files", "read_file", "search_text", "web_search", "read_skill", "propose_file_change", "generate_image", "run_command",
        };

        public static string AuthorizationHeader(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return string.Empty;
            }

            if (value.Length > 500)
            {
                value = value.Substring(0, 500);
            }

            if (value.IndexOf(' ') >= 0)
            {
                return value;
            }

            return "Bearer " + value;
        }

        public static async Task<McpSession> OpenAsync(HttpClient http, string url, string authorization, CancellationToken cancellationToken)
        {
            if (http == null)
            {
                throw new ArgumentNullException(nameof(http));
            }

            if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException("MCP needs an http or https link.");
            }

            var session = new McpSession(http, uri.AbsoluteUri, AuthorizationHeader(authorization), string.Empty, new List<McpTool>(), 1);
            var initialized = await session.SendAsync("initialize", new JObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "Arni", ["version"] = "0.1" },
            }, true, cancellationToken).ConfigureAwait(false);
            session.SetInstructions((string)initialized?["instructions"] ?? string.Empty);
            await session.SendAsync("notifications/initialized", new JObject(), false, cancellationToken).ConfigureAwait(false);
            var tools = new List<McpTool>();
            string cursor = null;
            for (var page = 0; page < 3; page++)
            {
                var parameters = new JObject();
                if (!string.IsNullOrEmpty(cursor))
                {
                    parameters["cursor"] = cursor;
                }

                var listed = await session.SendAsync("tools/list", parameters, true, cancellationToken).ConfigureAwait(false);
                AddTools(listed?["tools"] as JArray, tools);
                cursor = ((string)listed?["nextCursor"] ?? string.Empty).Trim();
                if (cursor.Length == 0 || tools.Count >= 32)
                {
                    break;
                }
            }

            session.ReplaceTools(tools);
            return session;
        }

        private static void AddTools(JArray source, List<McpTool> tools)
        {
            if (source == null)
            {
                return;
            }

            foreach (var item in source)
            {
                if (tools.Count >= 32 || item.Type != JTokenType.Object)
                {
                    continue;
                }

                var name = ((string)item["name"] ?? string.Empty).Trim();
                if (name.Length == 0 || name.Length > 64 || OwnTools.Contains(name) || !IsToolName(name) || ContainsName(tools, name))
                {
                    continue;
                }

                var description = ((string)item["description"] ?? string.Empty).Trim().Replace("\r", " ").Replace("\n", " ");
                if (description.Length > 400)
                {
                    description = description.Substring(0, 400);
                }

                var schema = SimpleSchema(item["inputSchema"] as JObject);
                tools.Add(new McpTool(name, description, schema));
            }
        }

        private static JObject SimpleSchema(JObject schema)
        {
            return SimpleSchema(schema, 0);
        }

        private static JObject SimpleSchema(JObject schema, int depth)
        {
            var copy = new JObject { ["type"] = "object" };
            if (schema == null || depth > 4)
            {
                return copy;
            }

            var type = ((string)schema["type"] ?? string.Empty).Trim();
            if (type == "object" || type == "string" || type == "integer" || type == "number" || type == "boolean" || type == "array")
            {
                copy["type"] = type;
            }

            var description = ((string)schema["description"] ?? string.Empty).Trim();
            if (description.Length > 180)
            {
                description = description.Substring(0, 180);
            }

            if (description.Length > 0)
            {
                copy["description"] = description;
            }

            if (schema["enum"] is JArray values && values.Count > 0 && values.Count <= 12)
            {
                copy["enum"] = values.DeepClone();
            }

            if (schema["properties"] is JObject properties)
            {
                var kept = new JObject();
                foreach (var property in properties.Properties())
                {
                    if (property.Value is JObject child)
                    {
                        kept[property.Name] = SimpleSchema(child, depth + 1);
                    }
                }

                copy["properties"] = kept;
            }

            if (schema["items"] is JObject items)
            {
                copy["items"] = SimpleSchema(items, depth + 1);
            }

            if (schema["required"] is JArray required && required.Count > 0 && required.Count <= 12)
            {
                copy["required"] = required.DeepClone();
            }

            return copy;
        }

        private static bool ContainsName(List<McpTool> tools, string name)
        {
            for (var i = 0; i < tools.Count; i++)
            {
                if (string.Equals(tools[i].Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsToolName(string name)
        {
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
