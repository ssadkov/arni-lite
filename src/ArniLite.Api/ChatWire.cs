using ArniLite.Core;
using Newtonsoft.Json.Linq;

namespace ArniLite.Api
{
    public static class ChatWire
    {
        public static JToken Content(ChatMessage message)
        {
            var images = message?.Images;
            if (images == null || images.Count == 0)
            {
                return message?.Content ?? string.Empty;
            }

            var parts = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = message.Content ?? string.Empty },
            };
            for (var i = 0; i < images.Count; i++)
            {
                var image = images[i];
                var media = image != null && image.MediaType == "image/jpeg" ? "image/jpeg" : "image/png";
                var data = image?.DataBase64 ?? string.Empty;
                if (data.Length == 0)
                {
                    continue;
                }

                parts.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject { ["url"] = "data:" + media + ";base64," + data },
                });
            }

            return parts;
        }
    }
}
