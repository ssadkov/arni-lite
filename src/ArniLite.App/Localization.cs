using System;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using ArniLite.Core;

namespace ArniLite
{
    public sealed class UiStrings : INotifyPropertyChanged
    {
        public static UiStrings Current { get; } = new UiStrings();
        private UiStrings() { StringCatalog.LanguageChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")); }
        public string this[string key] => StringCatalog.Get(key);
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [MarkupExtensionReturnType(typeof(object))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension(string key) { Key = key; }
        public string Key { get; }
        public override object ProvideValue(IServiceProvider serviceProvider) =>
            new Binding("[" + Key + "]") { Source = UiStrings.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
    }
}
