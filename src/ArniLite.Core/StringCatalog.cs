using System;
using System.Globalization;
using System.Resources;

namespace ArniLite.Core
{
    public static class StringCatalog
    {
        private static readonly ResourceManager Resources = new ResourceManager("ArniLite.Core.Strings", typeof(StringCatalog).Assembly);
        private static CultureInfo _culture = CultureInfo.GetCultureInfo("ru-RU");

        public static event EventHandler? LanguageChanged;
        public static string Language => _culture.TwoLetterISOLanguageName;
        public static CultureInfo Culture => _culture;

        public static void SetLanguage(string language)
        {
            _culture = CultureInfo.GetCultureInfo(language == "en" ? "en-US" : language == "kk" ? "kk-KZ" : "ru-RU");
            CultureInfo.DefaultThreadCurrentUICulture = _culture;
            CultureInfo.DefaultThreadCurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _culture;
            CultureInfo.CurrentCulture = _culture;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        public static string Get(string key, params object[] args)
        {
            var text = Resources.GetString(key, _culture) ?? key;
            return args.Length == 0 ? text : string.Format(_culture, text, args);
        }
    }
}
