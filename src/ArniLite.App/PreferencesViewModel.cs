using System;
using System.Collections.Generic;
using System.IO;
using ArniLite.Core;
using ArniLite.Storage;

namespace ArniLite
{
    public sealed class LanguageChoice
    {
        public string Code { get; }
        public string Name { get; }
        public LanguageChoice(string code, string name) { Code = code; Name = name; }
    }

    public sealed class PreferencesViewModel : ViewModelBase
    {
        private readonly PreferencesStore _store;
        private bool _saveFailed;
        public PreferencesViewModel(PreferencesStore store) { _store = store; }
        public IList<LanguageChoice> Languages { get; } = new[]
        {
            new LanguageChoice("ru", "Русский"), new LanguageChoice("en", "English"), new LanguageChoice("kk", "Қазақша"),
        };
        public string Language
        {
            get => _store.Value.Language;
            set
            {
                if (value == null || value == _store.Value.Language) return;
                _store.Value.Language = value == "en" || value == "kk" ? value : "ru";
                StringCatalog.SetLanguage(_store.Value.Language);
                Notify(nameof(Language));
                Save();
            }
        }
        public bool WrapText
        {
            get => _store.Value.WrapText;
            set { _store.Value.WrapText = value; Notify(nameof(WrapText)); Save(); }
        }
        public string SaveError => _saveFailed ? StringCatalog.Get("PreferencesFailed") : string.Empty;

        private void Save()
        {
            try { _store.Save(); _saveFailed = false; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _saveFailed = true;
                DiagnosticLog.Current.Write("preferences_save_failed", exception: ex);
            }
            Notify(nameof(SaveError));
        }
    }
}
