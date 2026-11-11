using System;
using System.IO;
using System.Text;
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
