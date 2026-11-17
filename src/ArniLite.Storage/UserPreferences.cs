using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ArniLite.Core;
using Newtonsoft.Json;

namespace ArniLite.Storage
{
    public sealed class UserPreferences
    {
        public string Language { get; set; } = "ru";
        public bool WrapText { get; set; } = true;
        public bool CheckUpdatesAutomatically { get; set; } = true;
        public bool PreviewUpdates { get; set; }
        public DateTime? LastUpdateCheckUtc { get; set; }
        public string DictationLanguage { get; set; } = "ru";
        public int MicrophoneId { get; set; } = -1;
        public string McpUrl { get; set; } = string.Empty;
        public List<McpServerRecord> McpServers { get; set; } = new List<McpServerRecord>();
        [JsonIgnore]
        public string LegacySecretId { get; set; } = string.Empty;
    }

    public sealed class PreferencesStore
    {
        private readonly string _path;
        public UserPreferences Value { get; }

        public PreferencesStore(string? path = null)
        {
            _path = path ?? Path.Combine(AppPaths.DataDirectory, "preferences.json");
            try { Value = File.Exists(_path) ? JsonConvert.DeserializeObject<UserPreferences>(File.ReadAllText(_path)) ?? new UserPreferences() : new UserPreferences(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { Value = new UserPreferences(); }
            if (Value.Language != "en" && Value.Language != "kk") Value.Language = "ru";
            if (Value.DictationLanguage != "ru" && Value.DictationLanguage != "en" && Value.DictationLanguage != "kk" && Value.DictationLanguage != "auto") Value.DictationLanguage = "ru";
            var dirty = NormalizeServers(Value);
            if (dirty)
            {
                try { Save(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private static bool NormalizeServers(UserPreferences value)
        {
            var incoming = value.McpServers ?? new List<McpServerRecord>();
            var clean = new List<McpServerRecord>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < incoming.Count && clean.Count < McpAddress.MaxServers; i++)
            {
                var item = incoming[i];
                var url = McpAddress.Normalize(item?.Url);
                if (url.Length == 0 || !seen.Add(url))
                {
                    continue;
                }

                var id = item.Id ?? string.Empty;
                if (id.Length < 8 || id.Length > 40)
                {
                    id = Guid.NewGuid().ToString("N");
                }

                clean.Add(new McpServerRecord { Id = id, Url = url, Connected = item.Connected });
            }

            var legacy = McpAddress.Normalize(value.McpUrl);
            var dirty = !string.IsNullOrEmpty(value.McpUrl) || clean.Count != incoming.Count;
            if (!dirty)
            {
                for (var i = 0; i < clean.Count; i++)
                {
                    if (!string.Equals(clean[i].Id, incoming[i].Id, StringComparison.Ordinal) || !string.Equals(clean[i].Url, incoming[i].Url, StringComparison.Ordinal))
                    {
                        dirty = true;
                        break;
                    }
                }
            }

            if (legacy.Length > 0)
            {
                string owner = string.Empty;
                for (var i = 0; i < clean.Count; i++)
                {
                    if (string.Equals(clean[i].Url, legacy, StringComparison.OrdinalIgnoreCase))
                    {
                        owner = clean[i].Id;
                        break;
                    }
                }

                if (owner.Length == 0 && clean.Count < McpAddress.MaxServers)
                {
                    owner = Guid.NewGuid().ToString("N");
                    clean.Add(new McpServerRecord { Id = owner, Url = legacy, Connected = false });
                }

                value.LegacySecretId = owner;
                dirty = true;
            }

            value.McpServers = clean;
            value.McpUrl = string.Empty;
            return dirty;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(Value), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
        }
    }
}
