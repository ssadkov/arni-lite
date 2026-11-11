using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Storage;
using ArniLite.Windows;

namespace ArniLite
{
    public sealed class DictationViewModel : ViewModelBase, IDisposable
    {
        private readonly PreferencesStore _preferences;
        private readonly WhisperPackage _package;
        private readonly WhisperRecognizer _recognizer;
        private readonly Action<string> _insertText;
        private readonly Func<bool> _canStart;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private CancellationTokenSource? _operation;
        private MicrophoneRecorder? _recorder;
        private bool _busy, _disposed, _recognizing;
        private string _statusKey = "SpeechDescription";
        private object[] _statusArgs = Array.Empty<object>();
        private DateTime _started;
        private double _progress;
        private bool _refreshingDevices;
        public event EventHandler? StateChanged;
        public DictationViewModel(PreferencesStore preferences, VerifiedDownload download, Action<string> insertText, Func<bool> canStart)
        {
            _preferences = preferences; _package = new WhisperPackage(download); _recognizer = new WhisperRecognizer(_package, new ProcessRunner());
            _insertText = insertText; _canStart = canStart; _dispatcher = Dispatcher.CurrentDispatcher;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => { Notify(nameof(RecordingTime)); if (DateTime.UtcNow - _started >= TimeSpan.FromSeconds(MicrophoneRecorder.MaximumSeconds)) _ = FinishAsync(); };
            InstallCommand = new RelayCommand(async () => await InstallAsync(), () => !IsActive);
            RemoveCommand = new RelayCommand(Remove, () => !IsActive && IsInstalled);
            ToggleCommand = new RelayCommand(async () => await ToggleAsync(), () => IsRecording || (!IsBusy && _canStart()));
            CancelCommand = new RelayCommand(Cancel, () => IsActive);
            RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => !IsActive);
            StringCatalog.LanguageChanged += LanguageChanged;
        }
        public ICommand InstallCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand ToggleCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand RefreshDevicesCommand { get; }
        public ObservableCollection<MicrophoneDevice> Microphones { get; } = new ObservableCollection<MicrophoneDevice>();
        public IReadOnlyList<LanguageChoice> Languages => new[] { new LanguageChoice("ru", "Русский"), new LanguageChoice("en", "English"), new LanguageChoice("kk", "Қазақша"), new LanguageChoice("auto", StringCatalog.Get("SpeechAutomaticLanguage")) };
        public int MicrophoneId { get => _preferences.Value.MicrophoneId; set { if (_refreshingDevices || value == _preferences.Value.MicrophoneId) return; _preferences.Value.MicrophoneId = value; Save(); Notify(nameof(MicrophoneId)); } }
        public string Language { get => _preferences.Value.DictationLanguage; set { if (value == _preferences.Value.DictationLanguage || (value != "ru" && value != "en" && value != "kk" && value != "auto")) return; _preferences.Value.DictationLanguage = value; Save(); Notify(nameof(Language)); } }
        public bool IsInstalled => _package.IsInstalled;
        public bool IsRecording => _recorder != null;
        public bool IsBusy => _busy;
        public bool IsActive => _busy || IsRecording;
        public bool BlocksInput => IsRecording || _recognizing;
        public bool HasStatus => _statusKey != "SpeechDescription";
        public double Progress => _progress;
        public string Status => StringCatalog.Get(_statusKey, _statusArgs);
        public string InstallationStatus => StringCatalog.Get(IsInstalled ? "SpeechInstalled" : "SpeechNotInstalled");
        public string ToggleLabel => StringCatalog.Get(IsRecording ? "SpeechFinish" : "SpeechStart");
        public string RecordingTime => IsRecording ? ((int)(DateTime.UtcNow - _started).TotalSeconds).ToString() + " / 120 " + StringCatalog.Get("SecondsShort") : "";
        private void SetStatus(string key, params object[] args) { _statusKey = key; _statusArgs = args; Notify(nameof(Status)); Notify(nameof(HasStatus)); }
        private void Save()
        {
            try { _preferences.Save(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { DiagnosticLog.Current.Write("speech_preferences_failed", exception: ex); SetStatus("PreferencesFailed"); }
        }
        public void RefreshDevices()
        {
            try
            {
                var devices = MicrophoneRecorder.Devices();
                _refreshingDevices = true;
                Microphones.Clear(); Microphones.Add(new MicrophoneDevice(-1, StringCatalog.Get("SpeechDefaultMicrophone")));
                var found = MicrophoneId == -1;
                foreach (var device in devices) { Microphones.Add(device); if (device.Id == MicrophoneId) found = true; }
                _refreshingDevices = false;
                if (!found) MicrophoneId = -1;
                Notify(nameof(MicrophoneId));
            }
            catch (Exception ex) { DiagnosticLog.Current.Write("speech_devices_failed", exception: ex); SetStatus("SpeechMicrophoneFailed"); }
            finally { _refreshingDevices = false; }
        }
        private async Task InstallAsync()
        {
            _operation = new CancellationTokenSource(TimeSpan.FromMinutes(30)); _busy = true; _progress = 0; Changed(); SetStatus("SpeechDownloading");
            try
            {
                await _package.InstallAsync(new Progress<double>(p => { _progress = p; Notify(nameof(Progress)); }), _operation.Token);
                SetStatus("SpeechReady"); DiagnosticLog.Current.Write("speech_install_complete");
            }
            catch (OperationCanceledException) { SetStatus("OperationCancelled"); }
            catch (Exception ex) { DiagnosticLog.Current.Write("speech_install_failed", exception: ex); SetStatus("SpeechDownloadFailed"); }
            finally { _operation.Dispose(); _operation = null; _busy = false; Changed(); }
        }
        private void Remove()
        {
            try { _package.Remove(); SetStatus("SpeechNotInstalled"); }
            catch (Exception ex) { DiagnosticLog.Current.Write("speech_remove_failed", exception: ex); SetStatus("SpeechRemoveFailed"); }
            Changed();
        }
        private async Task ToggleAsync()
        {
            if (IsRecording) { await FinishAsync(); return; }
            if (!IsInstalled) { SetStatus("SpeechInstallFirst"); return; }
            try
            {
                _recorder = new MicrophoneRecorder();
                _recorder.LimitReached += LimitReached;
                _recorder.Start(MicrophoneId); _started = DateTime.UtcNow; _timer.Start(); SetStatus("SpeechRecording");
            }
            catch (Exception ex) { DiagnosticLog.Current.Write("speech_capture_failed", exception: ex); _recorder?.Dispose(); _recorder = null; SetStatus("SpeechMicrophoneFailed"); }
            Changed();
        }
        private void LimitReached(object? sender, EventArgs e) { _dispatcher.BeginInvoke(new Action(async () => { if (ReferenceEquals(sender, _recorder)) await FinishAsync(); })); }
        private async Task FinishAsync()
        {
            if (_recorder == null) return;
            var recorder = _recorder; _recorder = null; _timer.Stop(); _busy = true; _recognizing = true;
            _operation = new CancellationTokenSource(TimeSpan.FromMinutes(10)); Changed(); SetStatus("SpeechRecognizing");
            try
            {
                var recording = recorder.Stop();
                var text = await _recognizer.RecognizeAsync(recording, Language, _operation.Token);
                if (!_disposed && text.Length > 0) { _insertText(text); SetStatus("SpeechInserted"); }
                else SetStatus("SpeechNoWords");
            }
            catch (OperationCanceledException) { SetStatus("OperationCancelled"); }
            catch (Exception ex) { DiagnosticLog.Current.Write("speech_recognition_failed", exception: ex); SetStatus("SpeechRecognitionFailed"); }
            finally { recorder.Dispose(); _operation.Dispose(); _operation = null; _busy = false; _recognizing = false; Changed(); }
        }
        private void Cancel()
        {
            _operation?.Cancel(); _timer.Stop();
            if (_recorder != null) { _recorder.Dispose(); _recorder = null; SetStatus("OperationCancelled"); }
            Changed();
        }
        public void Changed()
        {
            foreach (var name in new[] { nameof(IsRecording), nameof(IsBusy), nameof(IsActive), nameof(IsInstalled), nameof(InstallationStatus), nameof(ToggleLabel), nameof(RecordingTime), nameof(Status), nameof(Progress) }) Notify(name);
            foreach (var command in new[] { InstallCommand, RemoveCommand, ToggleCommand, CancelCommand, RefreshDevicesCommand }) (command as RelayCommand)?.Raise();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        private void LanguageChanged(object? sender, EventArgs e) { Notify(nameof(Languages)); if (Microphones.Count > 0 && !IsActive) RefreshDevices(); Changed(); }
        public void Dispose() { _disposed = true; Cancel(); StringCatalog.LanguageChanged -= LanguageChanged; }
    }
}
