using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Windows;
using ArniLite.Storage;

namespace ArniLite
{
    public sealed class MainViewModel : ViewModelBase
    {
        private readonly ArniApiClient _api;
        private readonly IProcessRunner _runner;
        private readonly ITokenStore _store;
        private readonly OsFacts _os;
        private readonly HttpClient _publicDownloads = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private CancellationTokenSource _operation = new CancellationTokenSource();
        private string _deviceCode = string.Empty;
        private string _verificationUri = string.Empty;
        private string _statusLine;
        private string _userCode = string.Empty;
        private string _log = string.Empty;
        private bool _busy;
        private bool _signedIn;
        private bool _hasSignInLink;

        public MainViewModel(OsFacts os, PreferencesStore? preferences = null)
        {
            Preferences = new PreferencesViewModel(preferences ?? new PreferencesStore());
            _os = os ?? throw new ArgumentNullException(nameof(os));
            _api = new ArniApiClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
            _runner = new ProcessRunner();
            _store = new DpapiTokenStore();
            _statusLine = os.Description + " · Framework " + FrameworkProbe.Describe();
            SignInCommand = new RelayCommand(async () => await SignInAsync(), () => !_busy);
            CopyLinkCommand = new RelayCommand(CopyLink, () => !string.IsNullOrEmpty(_verificationUri));
            CopyCodeCommand = new RelayCommand(CopyCode, () => HasUserCode);
            PowerShellCommand = new RelayCommand(async () => await RunPowerShellAsync(), () => !_busy);
            StreamCommand = new RelayCommand(async () => await StreamAsync(), () => !_busy && HasToken());
            StopCommand = new RelayCommand(Stop, () => _busy);
            SignOutCommand = new RelayCommand(SignOut, () => Dictation == null || !Dictation.IsActive);
            CloseCommand = new RelayCommand(() => Application.Current.Shutdown());
            _signedIn = HasToken();
            Chat = new ChatSession(_api, _store, _os, _runner);
            _publicDownloads.DefaultRequestHeaders.UserAgent.ParseAdd("ArniLite/" + AppVersion.Display);
            var downloads = new VerifiedDownload(_publicDownloads);
            Dictation = new DictationViewModel(Preferences.Store, downloads, text => Chat.Draft = string.IsNullOrWhiteSpace(Chat.Draft) ? text : Chat.Draft.TrimEnd() + " " + text, () => !Chat.IsBusy);
            Updates = new UpdatesViewModel(Preferences.Store, downloads, os, () => !Chat.IsBusy && !Dictation.IsActive && !_busy, Chat.SaveDraftForRestart);
            Dictation.StateChanged += (_, _) => { Chat.SetInputLocked(Dictation.BlocksInput); Updates.Changed(); (SignOutCommand as RelayCommand)?.Raise(); };
            Chat.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Chat.IsBusy)) { Dictation.Changed(); Updates.Changed(); } };
            Append("Окно открыто. Диагностика сети выполняется в фоне.");
            _ = DiagnoseAsync();
            _ = Chat.LoadAccountAsync();
        }

        public string StatusLine
        {
            get => _statusLine;
            private set { _statusLine = value; Notify(nameof(StatusLine)); }
        }

        public string UserCode
        {
            get => _userCode;
            private set { _userCode = value; Notify(nameof(UserCode)); Notify(nameof(HasUserCode)); }
        }

        public bool HasUserCode => !string.IsNullOrEmpty(_userCode);

        public bool IsSignedIn
        {
            get => _signedIn;
            private set { _signedIn = value; Notify(nameof(IsSignedIn)); }
        }

        public bool HasSignInLink
        {
            get => _hasSignInLink;
            private set { _hasSignInLink = value; Notify(nameof(HasSignInLink)); }
        }

        public string Log
        {
            get => _log;
            private set { _log = value; Notify(nameof(Log)); }
        }

        public ICommand SignInCommand { get; }
        public ICommand CopyLinkCommand { get; }
        public ICommand CopyCodeCommand { get; }
        public ICommand PowerShellCommand { get; }
        public ICommand StreamCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand SignOutCommand { get; }
        public ICommand CloseCommand { get; }
        public ChatSession Chat { get; }
        public PreferencesViewModel Preferences { get; }
        public DictationViewModel Dictation { get; }
        public UpdatesViewModel Updates { get; }
        public void DisposeFeatures() { Dictation.Dispose(); Updates.Dispose(); _publicDownloads.Dispose(); }

        private bool HasToken()
        {
            try
            {
                return !string.IsNullOrEmpty(_store.Load());
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task DiagnoseAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var tls = await _api.ProbeTlsAsync(timeout.Token).ConfigureAwait(true);
                DiagnosticLog.Current.Write("tls_probe", phase: tls.Status == "pass" ? "pass" : "fail");
                Append(tls.Status == "pass" ? "HTTPS к api.arnion.ru установлен." : "HTTPS не установлен: " + tls.Detail);
                var policy = TlsBootstrap.Configure(_os);
                Append(policy);
            }
            catch (Exception ex)
            {
                Append("Диагностика не завершилась: " + ex.Message);
            }
        }

        private async Task SignInAsync()
        {
            DiagnosticLog.Current.Write("auth_start");
            var operation = Begin();
            try
            {
                var start = await _api.StartDesktopAuthAsync(operation.Token).ConfigureAwait(true);
                _deviceCode = start.DeviceCode;
                _verificationUri = start.VerificationUri;
                HasSignInLink = !string.IsNullOrEmpty(_verificationUri);
                UserCode = start.UserCode;
            Notify(nameof(HasUserCode));
            (CopyCodeCommand as RelayCommand)?.Raise();
                Append("Код входа показан выше. Сверьте его на странице и введите вручную. Ссылка не содержит секрет устройства.");
                ExternalBrowser.Open(start.VerificationUri);
                var interval = Math.Max(1, start.IntervalSeconds);
                while (!operation.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), operation.Token).ConfigureAwait(true);
                    var poll = await _api.PollDesktopAuthAsync(_deviceCode, operation.Token).ConfigureAwait(true);
                    interval = Math.Max(1, poll.IntervalSeconds);
                    if (poll.Status == "pending" || poll.Status == "slow_down")
                    {
                        continue;
                    }

                    if (poll.Status == "approved" && !string.IsNullOrEmpty(poll.Token))
                    {
                        _store.Save(poll.Token);
                        _deviceCode = string.Empty;
                        _verificationUri = string.Empty;
                        HasSignInLink = false;
                        UserCode = string.Empty;
                        IsSignedIn = true;
                        DiagnosticLog.Current.Write("auth_approved");
                        Append("Вход выполнен. Токен сохранён через DPAPI и в журнал не пишется.");
                        RefreshCommands();
                        _ = Chat.LoadAccountAsync();
                        return;
                    }

                    _deviceCode = string.Empty;
                    Append("Вход завершился со статусом " + poll.Status + ".");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                _deviceCode = string.Empty;
                DiagnosticLog.Current.Write("auth_cancelled");
                Append("Вход остановлен.");
            }
            catch (Exception ex)
            {
                _deviceCode = string.Empty;
                DiagnosticLog.Current.Write("auth_failed", exception: ex, httpStatus: (ex as ApiCallException)?.StatusCode);
                Append(Redact(ex.Message));
            }
            finally
            {
                _busy = false;
                if (!IsSignedIn)
                {
                    HasSignInLink = !string.IsNullOrEmpty(_verificationUri);
                }

                RefreshCommands();
            }
        }

        private async Task RunPowerShellAsync()
        {
            var operation = Begin();
            try
            {
                Append("Запуск Windows PowerShell. Команда фиксированная, это проверка, а не терминал.");
                var result = await _runner.RunPowerShellAsync("Write-Output 'arni-lite-ok'\nWrite-Output 'проверка'\n", 20000, operation.Token).ConfigureAwait(true);
                Append(Describe(result));
            }
            catch (OperationCanceledException)
            {
                Append("Команда остановлена.");
            }
            catch (Exception ex)
            {
                Append(ex.Message);
            }
            finally
            {
                _busy = false;
                RefreshCommands();
            }
        }

        private async Task StreamAsync()
        {
            var session = _store.Load() ?? string.Empty;
            if (session.Length == 0)
            {
                Append("Сначала войдите.");
                return;
            }
            var operation = Begin();
            try
            {
                var model = await _api.FirstModelAsync(session, operation.Token).ConfigureAwait(true);
                Append("Поток модели " + model + ".");
                var chatId = "arni-lite:" + Guid.NewGuid().ToString("D");
                await _api.StreamOnceAsync(session, model, chatId, "Reply with the single word ok.", text => Append(text), operation.Token).ConfigureAwait(true);
                Append("Поток завершён.");
            }
            catch (OperationCanceledException)
            {
                Append("Поток остановлен. Повтор не отправляется.");
            }
            catch (Exception ex)
            {
                Append(Redact(ex.Message));
            }
            finally
            {
                _busy = false;
                RefreshCommands();
            }
        }

        private void CopyLink()
        {
            if (string.IsNullOrEmpty(_verificationUri))
            {
                return;
            }

            Clipboard.SetText(_verificationUri);
            Append("Ссылка скопирована.");
        }

        private void CopyCode()
        {
            if (string.IsNullOrEmpty(_userCode))
            {
                return;
            }

            Clipboard.SetText(_userCode);
            Append("Код входа скопирован.");
        }

        private void SignOut()
        {
            DiagnosticLog.Current.Write("auth_signed_out");
            Chat.Stop();
            Stop();
            _store.Clear();
            _deviceCode = string.Empty;
            _verificationUri = string.Empty;
            HasSignInLink = false;
            UserCode = string.Empty;
            IsSignedIn = false;
            Chat.ClearAccount();
            Append("Локальный токен удалён. Это не отзывает уже выданные серверные JWT.");
            RefreshCommands();
        }

        private void Stop()
        {
            try
            {
                _operation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private CancellationTokenSource Begin()
        {
            var previous = _operation;
            _operation = new CancellationTokenSource();
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _busy = true;
            RefreshCommands();
            return _operation;
        }

        private void RefreshCommands()
        {
            (SignInCommand as RelayCommand)?.Raise();
            (CopyLinkCommand as RelayCommand)?.Raise();
            (CopyCodeCommand as RelayCommand)?.Raise();
            (PowerShellCommand as RelayCommand)?.Raise();
            (StreamCommand as RelayCommand)?.Raise();
            (StopCommand as RelayCommand)?.Raise();
        }

        private void Append(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            Log = string.IsNullOrEmpty(Log) ? line : Log + Environment.NewLine + line;
        }

        private string Redact(string message)
        {
            if (string.IsNullOrEmpty(_deviceCode) || string.IsNullOrEmpty(message))
            {
                return message;
            }

            return message.Replace(_deviceCode, "[redacted]");
        }

        private static string Describe(ProcessRunResult result)
        {
            var text = new StringBuilder();
            text.Append(result.Status);
            if (result.ExitCode.HasValue)
            {
                text.Append(", код ").Append(result.ExitCode.Value);
            }

            text.Append(", ").Append(result.DurationMs).Append(" мс. ").Append(result.JobDetail);
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                text.AppendLine().Append(result.Stdout.Trim());
            }

            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                text.AppendLine().Append(result.Stderr.Trim());
            }

            if (result.Survivors.Count > 0)
            {
                text.AppendLine().Append("Остались процессы: ").Append(string.Join(", ", result.Survivors));
            }

            return text.ToString();
        }
    }

    public abstract class ViewModelBase : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        protected void Notify(string name)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

        public void Execute(object parameter) => _execute();

        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
