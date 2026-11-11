using System;
using System.IO;
using System.Windows;
using ArniLite.Core;
using ArniLite.Storage;
using Microsoft.Win32;

namespace ArniLite
{
    public partial class SettingsWindow : Window
    {
        private string _exportStatusKey = string.Empty;
        private string _logStatusKey = string.Empty;
        public SettingsWindow(MainViewModel model, bool updates = false, bool speech = false)
        {
            InitializeComponent();
            DataContext = model;
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);
            RefreshLog();
            model.Dictation.RefreshDevices();
            if (updates) SettingsTabs.SelectedIndex = 2;
            else if (speech) SettingsTabs.SelectedIndex = 1;
            StringCatalog.LanguageChanged += LanguageChanged;
            Closed += (_, _) => StringCatalog.LanguageChanged -= LanguageChanged;
        }

        private void LanguageChanged(object? sender, EventArgs e)
        {
            AboutEnvironment.Text = AppVersion.EnvironmentSummary;
            if (_exportStatusKey.Length > 0) ExportStatus.Text = StringCatalog.Get(_exportStatusKey);
            if (_logStatusKey.Length > 0) LogText.Text = StringCatalog.Get(_logStatusKey);
        }

        private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
        private void Releases_OnClick(object sender, RoutedEventArgs e) => ArniLite.Windows.ExternalBrowser.Open("https://github.com/ssadkov/arni-lite/releases");
        private void Refresh_OnClick(object sender, RoutedEventArgs e) => RefreshLog();
        private void RefreshLog()
        {
            try
            {
                var text = DiagnosticLog.Current.ReadRecent();
                _logStatusKey = text.Length == 0 ? "LogsEmpty" : string.Empty;
                LogText.Text = text.Length == 0 ? StringCatalog.Get("LogsEmpty") : text;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LogText.Text = StringCatalog.Get("LogsFailed");
                _logStatusKey = "LogsFailed";
            }
        }

        private void Export_OnClick(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                FileName = "ArniLite-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip",
                DefaultExt = ".zip", Filter = "ZIP (*.zip)|*.zip",
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                DiagnosticLog.Current.Export(dialog.FileName, AppVersion.EnvironmentSummary);
                ExportStatus.Text = StringCatalog.Get("LogsSaved");
                _exportStatusKey = "LogsSaved";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DiagnosticLog.Current.Write("diagnostics_export_failed", exception: ex);
                ExportStatus.Text = StringCatalog.Get("LogsFailed");
                _exportStatusKey = "LogsFailed";
            }
        }
    }
}
