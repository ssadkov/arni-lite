using System;
using Newtonsoft.Json.Linq;

namespace ArniLite.Storage
{
    public static class McpSecretMap
    {
        public static bool IsDocument(string raw)
        {
            return !string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith("{", StringComparison.Ordinal);
        }

        public static string Read(string document, string id)
        {
            var map = Parse(document);
            var token = map[id];
            return token != null && token.Type == JTokenType.String ? (string)token : string.Empty;
        }

        public static string Upsert(string document, string id, string token)
        {
            RequireId(id);
            var map = Parse(document);
            map[id] = token ?? string.Empty;
            return map.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string Remove(string document, string id)
        {
            var map = Parse(document);
            map.Remove(id);
            return map.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static bool IsEmpty(string document)
        {
            foreach (var _ in Parse(document).Properties())
            {
                return false;
            }

            return true;
        }

        private static JObject Parse(string document)
        {
            try
            {
                return JObject.Parse(string.IsNullOrWhiteSpace(document) ? "{}" : document);
            }
            catch (Exception)
            {
                return new JObject();
            }
        }

        private static void RequireId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 8 || id.Length > 40)
            {
                throw new ArgumentException("MCP id is invalid.");
            }

            for (var i = 0; i < id.Length; i++)
            {
                var ch = id[i];
                var ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
                if (!ok)
                {
                    throw new ArgumentException("MCP id is invalid.");
                }
            }
        }
    }
}
