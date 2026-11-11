using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Storage;
using ArniLite.Windows;

namespace ArniLite
{
    public sealed class UpdatesViewModel : ViewModelBase, IDisposable
    {
        private readonly PreferencesStore _preferences;
        private readonly VerifiedDownload _download;
        private readonly Func<bool> _canInstall;
        private readonly Action _saveDraft;
        private readonly Version _windows;
        private CancellationTokenSource? _operation;
        private AvailableUpdate? _available;
        private string? _installer;
        private bool _busy;
        private bool _disposed;
        private string _statusKey = "UpdateDescription";
        private object[] _statusArgs = Array.Empty<object>();
        private double _progress;
        public UpdatesViewModel(PreferencesStore preferences, VerifiedDownload download, OsFacts os, Func<bool> canInstall, Action saveDraft)
        {
            _preferences = preferences; _download = download; _canInstall = canInstall; _saveDraft = saveDraft;
            _windows = new Version(os.Major, os.Minor, os.Build);
            CheckCommand = new RelayCommand(async () => await CheckAsync(), () => !_busy);
            DownloadCommand = new RelayCommand(async () => await DownloadAsync(), () => !_busy && _available != null && _installer == null);
            InstallCommand = new RelayCommand(Install, () => !_busy && _installer != null && _canInstall());
            CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => _busy);
            ReleaseCommand = new RelayCommand(() => ExternalBrowser.Open(_available!.ReleaseUri.ToString()), () => _available != null);
            StringCatalog.LanguageChanged += LanguageChanged;
        }
        public ICommand CheckCommand { get; }
        public ICommand DownloadCommand { get; }
        public ICommand InstallCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ReleaseCommand { get; }
        public bool Automatic { get => _preferences.Value.CheckUpdatesAutomatically; set { _preferences.Value.CheckUpdatesAutomatically = value; Save(); Notify(nameof(Automatic)); } }
        public bool Preview { get => _preferences.Value.PreviewUpdates; set { _preferences.Value.PreviewUpdates = value; _available = null; _installer = null; SetStatus("UpdateDescription"); Save(); Changed(); Notify(nameof(Preview)); } }
        public bool IsBusy => _busy;
        public bool HasUpdate => _available != null;
        public bool HasInstaller => _installer != null;
        public double Progress => _progress;
        public string Status => StringCatalog.Get(_statusKey, _statusArgs);
        public string LastChecked => _preferences.Value.LastUpdateCheckUtc?.ToLocalTime().ToString("g") ?? StringCatalog.Get("UpdateNeverChecked");
        public string AvailableVersion => _available?.Manifest.Version ?? "";
        public string AvailableLabel => StringCatalog.Get("UpdateAvailable", AvailableVersion);
        public async Task CheckAutomaticallyAsync()
        {
            await Task.Delay(3000);
            if (!_disposed && Automatic && (!_preferences.Value.LastUpdateCheckUtc.HasValue || DateTime.UtcNow - _preferences.Value.LastUpdateCheckUtc.Value >= TimeSpan.FromDays(1) || _preferences.Value.LastUpdateCheckUtc > DateTime.UtcNow)) await CheckAsync();
        }
        public async Task CheckAsync()
        {
            if (_busy || _disposed) return;
            _operation = new CancellationTokenSource(TimeSpan.FromSeconds(30)); _busy = true; _available = null; _installer = null; Changed(); SetStatus("UpdateChecking");
            try
            {
                _available = await new UpdateClient(_download, UpdateTrust.PublicKeyXml).CheckAsync(AppVersion.Display, AppVersion.Platform, _windows, Preview, _operation.Token);
                _preferences.Value.LastUpdateCheckUtc = DateTime.UtcNow; Save();
                SetStatus(_available == null ? "UpdateCurrent" : "UpdateAvailable", _available?.Manifest.Version ?? "");
                DiagnosticLog.Current.Write("update_check", phase: _available == null ? "current" : "available");
            }
            catch (OperationCanceledException) { SetStatus("OperationCancelled"); }
            catch (Exception ex) { DiagnosticLog.Current.Write("update_check_failed", exception: ex); SetStatus("UpdateCheckFailed"); }
            finally { _operation.Dispose(); _operation = null; _busy = false; Changed(); }
        }
        private async Task DownloadAsync()
        {
            if (_available == null || _busy) return;
            var update = _available;
            _operation = new CancellationTokenSource(TimeSpan.FromMinutes(15)); _busy = true; _progress = 0; Changed(); SetStatus("UpdateDownloading");
            try
            {
                var destination = Path.Combine(AppPaths.DataDirectory, "updates", update.Manifest.Version, update.Installer.FileName);
                await _download.DownloadAsync(update.DownloadUri, destination, update.Installer.Bytes, update.Installer.Sha256, new Progress<double>(p => { _progress = p; Notify(nameof(Progress)); }), _operation.Token);
                _installer = destination; SetStatus("UpdateReady");
            }
            catch (OperationCanceledException) { SetStatus("OperationCancelled"); }
            catch (Exception ex) { DiagnosticLog.Current.Write("update_download_failed", exception: ex); SetStatus("UpdateDownloadFailed"); }
            finally { _operation.Dispose(); _operation = null; _busy = false; Changed(); }
        }
        private void Install()
        {
            if (_installer == null || _available == null || !_canInstall()) return;
            try
            {
                VerifiedDownload.VerifyFile(_installer, _available.Installer.Bytes, _available.Installer.Sha256);
                _saveDraft(); // A save failure prevents shutdown/installation.
                var directory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var process = Process.Start(new ProcessStartInfo(_installer, "/ARNIUPDATE=1 /SILENT /SP- /NORESTART /NOCLOSEAPPLICATIONS /DIR=\"" + directory + "\"") { UseShellExecute = true });
                if (process == null) throw new InvalidOperationException("Installer did not start.");
                process.Dispose(); DiagnosticLog.Current.Write("update_install_start");
                Application.Current.Shutdown();
            }
            catch (Exception ex) { DiagnosticLog.Current.Write("update_install_failed", exception: ex); SetStatus("UpdateInstallFailed"); }
        }
        private void SetStatus(string key, params object[] args) { _statusKey = key; _statusArgs = args; Notify(nameof(Status)); }
        private void Save()
        {
            try { _preferences.Save(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { DiagnosticLog.Current.Write("update_preferences_failed", exception: ex); SetStatus("PreferencesFailed"); }
        }
        public void Changed()
        {
            foreach (var name in new[] { nameof(IsBusy), nameof(HasUpdate), nameof(HasInstaller), nameof(Progress), nameof(Status), nameof(LastChecked), nameof(AvailableVersion), nameof(AvailableLabel) }) Notify(name);
            foreach (var command in new[] { CheckCommand, DownloadCommand, InstallCommand, CancelCommand, ReleaseCommand }) (command as RelayCommand)?.Raise();
        }
        private void LanguageChanged(object? sender, EventArgs e) => Changed();
        public void Dispose() { _disposed = true; _operation?.Cancel(); StringCatalog.LanguageChanged -= LanguageChanged; }
    }
}
