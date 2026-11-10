using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ArniLite.Api
{
    public sealed class ChatDelta
    {
        public string Content { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string FinishReason { get; set; } = string.Empty;
        public bool Done { get; set; }
        public bool Malformed { get; set; }
        public List<ToolCallDelta> ToolCalls { get; } = new List<ToolCallDelta>();
    }

    public sealed class ToolCallDelta
    {
        public int Index { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
    }

    public static class ChatStreamParser
    {
        public static ChatDelta Parse(SseEvent ev)
        {
            if (ev == null)
            {
                throw new ArgumentNullException(nameof(ev));
            }

            var delta = new ChatDelta();
            if (ev.Data == "[DONE]")
            {
                delta.Done = true;
                return delta;
            }

            JObject json;
            try
            {
                json = JObject.Parse(ev.Data);
            }
            catch (Exception)
            {
                delta.Malformed = true;
                return delta;
            }

            var error = json["error"];
            if (error != null && error.Type != JTokenType.Null)
            {
                delta.Error = error.Type == JTokenType.String
                    ? error.ToString()
                    : (string?)error["message"] ?? (string?)error["code"] ?? "Request failed.";
                return delta;
            }

            var choice = json["choices"]?.First;
            ReadToolCalls(choice?["delta"]?["tool_calls"], delta.ToolCalls);
            ReadToolCalls(choice?["message"]?["tool_calls"], delta.ToolCalls);
            var content = choice?["delta"]?["content"] ?? choice?["message"]?["content"];
            if (content != null && content.Type == JTokenType.String)
            {
                delta.Content = content.ToString();
            }

            delta.FinishReason = (string?)choice?["finish_reason"] ?? string.Empty;
            return delta;
        }

        private static void ReadToolCalls(JToken calls, List<ToolCallDelta> target)
        {
            if (!(calls is JArray array))
            {
                return;
            }

            foreach (var item in array)
            {
                var function = item["function"];
                target.Add(new ToolCallDelta
                {
                    Index = (int?)item["index"] ?? target.Count,
                    Id = (string?)item["id"] ?? string.Empty,
                    Name = (string?)function?["name"] ?? string.Empty,
                    Arguments = (string?)function?["arguments"] ?? string.Empty,
                });
            }
        }
    }

    public static class ApiErrorText
    {
        public static string FromBody(string body, int statusCode)
        {
            var message = Read(body);
            if (message != "Request failed." || statusCode <= 0)
            {
                return message;
            }

            var snippet = (body ?? string.Empty).Trim().Replace("\r", " ").Replace("\n", " ");
            if (snippet.Length > 160)
            {
                snippet = snippet.Substring(0, 160);
            }

            return snippet.Length == 0
                ? "Сервер ответил " + statusCode + " без текста."
                : "Сервер ответил " + statusCode + ": " + snippet;
        }

        private static string Read(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return "Request failed.";
            }

            try
            {
                var json = JObject.Parse(body);
                var error = json["error"];
                if (error == null || error.Type == JTokenType.Null)
                {
                    return "Request failed.";
                }

                if (error.Type == JTokenType.String)
                {
                    return error.ToString();
                }

                return (string?)error["message"] ?? (string?)error["code"] ?? "Request failed.";
            }
            catch (Exception)
            {
                return "Request failed.";
            }
        }
    }
}
