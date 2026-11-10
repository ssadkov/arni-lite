using System;
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
            var content = choice?["delta"]?["content"];
            if (content != null && content.Type == JTokenType.String)
            {
                delta.Content = content.ToString();
            }

            delta.FinishReason = (string?)choice?["finish_reason"] ?? string.Empty;
            return delta;
        }
    }

    public static class ApiErrorText
    {
        public static string FromBody(string body)
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
