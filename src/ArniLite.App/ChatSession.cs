using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Storage;
using ArniLite.Windows;
using Newtonsoft.Json.Linq;

namespace ArniLite
{
    public sealed class ChatSession : ViewModelBase, IUserApproval, IModelCompletion
    {
        private readonly ArniApiClient _api;
        private readonly ITokenStore _tokens;
        private readonly OsFacts _os;
        private readonly WorkspaceService _workspace = new WorkspaceService();
        private readonly LocalToolHost _tools;
        private readonly JsonlJournal _journal;
        private readonly ChatHistory _historyStore;
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private readonly SynchronizationContext _ui;
        private readonly string _dataDirectory;
        private readonly DiagnosticLog _diagnostics;
        private readonly DispatcherTimer _streamTimer;
        private readonly object _streamGate = new object();
        private readonly StringBuilder _pendingText = new StringBuilder();
        private readonly List<TranscriptRow> _streamRows = new List<TranscriptRow>();
        private readonly HashSet<string> _collapsedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _turnCancellation;
        private string _search = string.Empty;
        private SideEntry? _selectedEntry;
        private string _filePreviewTitle = string.Empty;
        private string _filePreviewBody = string.Empty;
        private string _filePreviewPath = string.Empty;
        private PreviewKind _previewKind;
        private TaskCompletionSource<bool> _approval;
        private TranscriptRow _liveRow;
        private string _chatId;
        private string _draft = string.Empty;
        private string _projectLabel = "Проекты сохраняются в: не выбрано";
        private string _preview = string.Empty;
        private AccountInfo? _account;
        private string _accountError = string.Empty;
        private bool _accountLoading;
        private int _accountGeneration;
        private string _modelStatus = "ModelAfterSignIn";
        private readonly List<CatalogModel> _catalog = new List<CatalogModel>();
        private CatalogModel _selectedCatalog;
        private string _selectedModel = string.Empty;
        private bool _freeOnly = true;
        private bool _permissionVisible;
        private string _permissionTitle = string.Empty;
        private string _permissionBody = string.Empty;
        private bool _trust = true;
        private bool _busy;
        private string _activity = string.Empty;
        private bool _showLoader;

        public ChatSession(ArniApiClient api, ITokenStore tokens, OsFacts os, IProcessRunner processes, string? dataDirectory = null)
        {
            _api = api;
            _tokens = tokens;
            _os = os;
            _ui = SynchronizationContext.Current;
            _streamTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
            _streamTimer.Tick += (_, _) => FlushPendingText();
            _dataDirectory = dataDirectory ?? AppPaths.DataDirectory;
            _diagnostics = dataDirectory == null ? DiagnosticLog.Current : new DiagnosticLog(Path.Combine(_dataDirectory, "logs"));
            Directory.CreateDirectory(_dataDirectory);
            _journal = new JsonlJournal(Path.Combine(_dataDirectory, "side-effects.jsonl"));
            _journal.MarkExecutingAsUnknown();
            _historyStore = new ChatHistory(Path.Combine(_dataDirectory, "chats"));
            _tools = new LocalToolHost(_workspace, processes, _journal, this);
            _chatId = _historyStore.CurrentChatId();
            LoadCurrentChat();
            RefreshLists();

            OpenChatCommand = new ParamCommand(OpenChat);
            OpenFolderCommand = new ParamCommand(OpenFolder);
            ToggleProjectCommand = new ParamCommand(ToggleProject);
            NewProjectChatCommand = new ParamCommand(NewProjectChat);
            OpenInExplorerCommand = new RelayCommand(OpenInExplorer, () => !string.IsNullOrEmpty(_workspace.Root));
            OpenProjectFileCommand = new ParamCommand(OpenProjectFile);
            SendCommand = new RelayCommand(async () => await SendAsync(), () => !_busy && _selectedCatalog != null && !string.IsNullOrWhiteSpace(Draft));
            NewChatCommand = new RelayCommand(NewChat, () => !_busy);
            StopCommand = new RelayCommand(Stop, () => _busy);
            ClosePreviewCommand = new RelayCommand(ClosePreview);
            ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !_busy);
            AllowCommand = new RelayCommand(() => Resolve(true), () => _permissionVisible);
            DenyCommand = new RelayCommand(() => Resolve(false), () => _permissionVisible);
            RefreshAccountCommand = new RelayCommand(async () =>
            {
                if (Models.Count == 0) await LoadAccountAsync();
                else await RefreshAccountAsync();
            }, () => !_accountLoading && !_busy);
        }

        public ObservableCollection<TranscriptRow> Items { get; } = new ObservableCollection<TranscriptRow>();
        public ObservableCollection<CatalogModel> Models { get; } = new ObservableCollection<CatalogModel>();
        public ObservableCollection<ChatSummary> Chats { get; } = new ObservableCollection<ChatSummary>();
        public ObservableCollection<string> Folders { get; } = new ObservableCollection<string>();
        public ObservableCollection<SideEntry> ChatRows { get; } = new ObservableCollection<SideEntry>();
        public ObservableCollection<string> ProjectFiles { get; } = new ObservableCollection<string>();
        public ICommand OpenInExplorerCommand { get; }
        public ICommand OpenProjectFileCommand { get; }
        public ICommand OpenChatCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand ToggleProjectCommand { get; }
        public ICommand NewProjectChatCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ClosePreviewCommand { get; }
        public ICommand SendCommand { get; }
        public ICommand NewChatCommand { get; }
        public ICommand ChooseFolderCommand { get; }
        public ICommand AllowCommand { get; }
        public ICommand DenyCommand { get; }
        public ICommand RefreshAccountCommand { get; }
        public bool AllowSideEffectsForSession => _trust;
        public bool IsBusy => _busy;
        public bool CanChangeContext => !_busy;
        public string ProjectPath => _workspace.Root;
        public bool HasProject => !string.IsNullOrEmpty(_workspace.Root);
        public string ProjectName => HasProject ? Path.GetFileName(_workspace.Root.TrimEnd('\\')) : StringCatalog.Get("NoProject");
        public string ChatTitle
        {
            get
            {
                foreach (var message in _history)
                {
                    if (message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content))
                    {
                        var title = message.Content.Replace("\r", " ").Replace("\n", " ").Trim();
                        return title.Length > 70 ? title.Substring(0, 70) + "…" : title;
                    }
                }

                return StringCatalog.Get("NewChat");
            }
        }

        public string Search
        {
            get => _search;
            set { _search = value ?? string.Empty; Notify(nameof(Search)); RebuildChatRows(); }
        }

        public SideEntry? SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                _selectedEntry = value;
                Notify(nameof(SelectedEntry));
                if (value != null && !value.IsHeader) OpenChat(value.ChatId);
            }
        }

        public string FilePreviewTitle => _filePreviewTitle;
        public string FilePreviewBody => _filePreviewBody;
        public bool HasFilePreview => _filePreviewTitle.Length > 0;
        public bool HasTextPreview => HasFilePreview && _previewKind == PreviewKind.Text;
        public bool HasMediaPreview => HasFilePreview && _previewKind != PreviewKind.Text;
        public bool IsAudioPreview => _previewKind == PreviewKind.Audio;
        public string FilePreviewPath => _filePreviewPath;

        private void ClosePreview()
        {
            _filePreviewTitle = string.Empty;
            _filePreviewBody = string.Empty;
            _filePreviewPath = string.Empty;
            _previewKind = PreviewKind.Text;
            Notify(nameof(FilePreviewTitle));
            Notify(nameof(FilePreviewBody));
            Notify(nameof(HasFilePreview));
            NotifyPreview();
        }

        private void NotifyPreview()
        {
            Notify(nameof(HasTextPreview));
            Notify(nameof(HasMediaPreview));
            Notify(nameof(IsAudioPreview));
            Notify(nameof(FilePreviewPath));
        }

        public void RefreshLocalization()
        {
            if (_account != null) _account = _account.Relocalize();
            NotifyAccount();
            Notify(nameof(ModelStatus));
            Notify(nameof(ChatTitle));
            Notify(nameof(ProjectName));
            foreach (var model in Models) model.RefreshLocalization();
            foreach (var row in Items) row.RefreshLocalization();
            RefreshLists();
        }

        public string Draft
        {
            get => _draft;
            set { _draft = value; Notify(nameof(Draft)); (SendCommand as RelayCommand)?.Raise(); }
        }

        public CatalogModel SelectedCatalog
        {
            get => _selectedCatalog;
            set
            {
                if (_busy) return;
                _selectedCatalog = value;
                if (value != null)
                {
                    _selectedModel = value.Id;
                }

                Notify(nameof(SelectedCatalog));
                Notify(nameof(SelectedModel));
                (SendCommand as RelayCommand)?.Raise();
            }
        }

        public bool FreeOnly
        {
            get => _freeOnly;
            set
            {
                _freeOnly = value;
                Notify(nameof(FreeOnly));
                ApplyModelFilter();
            }
        }

        public string SelectedModel
        {
            get => _selectedModel;
            set { _selectedModel = value ?? string.Empty; Notify(nameof(SelectedModel)); }
        }

        public string ProjectLabel
        {
            get => _projectLabel;
            private set { _projectLabel = value; Notify(nameof(ProjectLabel)); }
        }

        public string Preview
        {
            get => _preview;
            private set { _preview = value; Notify(nameof(Preview)); }
        }

        public AccountInfo? Account => _account;
        public string AccountTitle => !string.IsNullOrEmpty(_account?.Email) ? _account.Email : StringCatalog.Get("AccountArni");
        public string AccountError => _accountError.Length == 0 ? string.Empty : StringCatalog.Get(_accountError);
        public bool AccountLoading => _accountLoading;
        public string ModelStatus => _modelStatus.Length == 0 ? string.Empty : StringCatalog.Get(_modelStatus);

        public void ClearAccount()
        {
            _accountGeneration++;
            _account = null;
            _accountError = string.Empty;
            _accountLoading = false;
            _catalog.Clear();
            Models.Clear();
            _selectedCatalog = null;
            _selectedModel = string.Empty;
            _modelStatus = "ModelAfterSignIn";
            Notify(nameof(SelectedCatalog));
            Notify(nameof(SelectedModel));
            Notify(nameof(ModelStatus));
            NotifyAccount();
        }

        public bool PermissionVisible
        {
            get => _permissionVisible;
            private set { _permissionVisible = value; Notify(nameof(PermissionVisible)); }
        }

        public string PermissionTitle
        {
            get => _permissionTitle;
            private set { _permissionTitle = value; Notify(nameof(PermissionTitle)); }
        }

        public string PermissionBody
        {
            get => _permissionBody;
            private set { _permissionBody = value; Notify(nameof(PermissionBody)); Preview = value; }
        }

        public bool TrustSession
        {
            get => _trust;
            set { _trust = value; Notify(nameof(TrustSession)); }
        }

        public string Activity
        {
            get => _activity;
            private set { _activity = value; Notify(nameof(Activity)); }
        }

        public bool ShowLoader
        {
            get => _showLoader;
            private set { _showLoader = value; Notify(nameof(ShowLoader)); }
        }

        public bool HasActivity => !string.IsNullOrEmpty(_activity);

        private void ApplyModelFilter()
        {
            var currentId = _selectedCatalog == null ? _selectedModel : _selectedCatalog.Id;
            Models.Clear();
            for (var i = 0; i < _catalog.Count; i++)
            {
                if (_freeOnly && !_catalog[i].Free)
                {
                    continue;
                }

                Models.Add(_catalog[i]);
            }

            CatalogModel match = null;
            for (var i = 0; i < Models.Count; i++)
            {
                if (Models[i].Id == currentId)
                {
                    match = Models[i];
                    break;
                }
            }

            SelectedCatalog = match ?? (Models.Count == 0 ? null : Models[0]);
        }

        public event System.Action TextGrew;

        public async Task LoadAccountAsync()
        {
            var token = _tokens.Load();
            if (string.IsNullOrEmpty(token))
            {
                return;
            }

            await Task.WhenAll(LoadModelsAsync(token, _accountGeneration), RefreshAccountAsync());
        }

        private async Task LoadModelsAsync(string token, int generation)
        {
            try
            {
                var models = await _api.ListModelsAsync(token, CancellationToken.None).ConfigureAwait(true);
                if (generation != _accountGeneration) return;
                _catalog.Clear();
                _catalog.AddRange(models);
                ApplyModelFilter();
                _modelStatus = Models.Count == 0 ? "NoModels" : string.Empty;
            }
            catch (Exception)
            {
                if (generation != _accountGeneration) return;
                _modelStatus = "ModelsUnavailable";
            }
            Notify(nameof(ModelStatus));
        }

        public async Task RefreshAccountAsync()
        {
            var token = _tokens.Load();
            if (_accountLoading || string.IsNullOrEmpty(token)) return;
            var generation = _accountGeneration;
            _accountLoading = true;
            _accountError = string.Empty;
            NotifyAccount();
            try
            {
                var account = await _api.AccountAsync(token, CancellationToken.None).ConfigureAwait(true);
                if (generation == _accountGeneration) _account = account;
            }
            catch (Exception ex)
            {
                _diagnostics.Write("account_refresh_failed", exception: ex, httpStatus: (ex as ApiCallException)?.StatusCode);
                if (generation == _accountGeneration)
                    _accountError = ex is ApiCallException api && api.StatusCode == 401
                        ? "AuthExpired"
                        : "AccountRefreshFailed";
            }
            finally
            {
                if (generation == _accountGeneration)
                {
                    _accountLoading = false;
                    NotifyAccount();
                }
            }
        }

        private void NotifyAccount()
        {
            Notify(nameof(Account));
            Notify(nameof(AccountTitle));
            Notify(nameof(AccountError));
            Notify(nameof(AccountLoading));
            (RefreshAccountCommand as RelayCommand)?.Raise();
        }

        public async Task<ModelTurn> CompleteAsync(IList<ChatMessage> messages, bool withTools, Action<string> onDelta, CancellationToken cancellationToken)
        {
            var token = _tokens.Load();
            if (string.IsNullOrEmpty(token))
            {
                return new ModelTurn { Error = StringCatalog.Get("SignInFirst"), Done = true, FinishReason = "error" };
            }

            Post(() => SetActivity(StringCatalog.Get("Thinking"), true));
            var model = string.IsNullOrEmpty(_selectedModel) ? (_selectedCatalog == null ? string.Empty : _selectedCatalog.Id) : _selectedModel;
            if (string.IsNullOrEmpty(model))
            {
                return new ModelTurn { Error = StringCatalog.Get("SelectModel"), Done = true, FinishReason = "error" };
            }

            return await _api.CompleteAsync(token, model, "arni-lite:" + _chatId, messages, withTools, onDelta, cancellationToken).ConfigureAwait(true);
        }

        public Task<bool> ApproveAsync(string title, string body, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<bool>();
            Post(() =>
            {
                SetActivity(StringCatalog.Get("WaitingApproval"), false);
                PermissionTitle = title;
                PermissionBody = body;
                PermissionVisible = true;
                _approval = source;
                (AllowCommand as RelayCommand)?.Raise();
                (DenyCommand as RelayCommand)?.Raise();
            });
            cancellationToken.Register(() => source.TrySetResult(false));
            return source.Task;
        }

        private async Task SendAsync()
        {
            var text = (Draft ?? string.Empty).Trim();
            if (text.Length == 0 || _busy)
            {
                return;
            }

            _busy = true;
            _diagnostics.Write("turn_start", phase: HasProject ? "project" : "general");
            _turnCancellation = new CancellationTokenSource();
            _streamTimer.Start();
            SetActivity(StringCatalog.Get("Thinking"), true);
            Draft = string.Empty;
            RaiseAll();
            try
            {
                EnsureSystem();
                var user = new ChatMessage { Role = "user", Content = text };
                _history.Add(user);
                _historyStore.Append(_chatId, user);
                AddRow(user);
                Notify(nameof(ChatTitle));
                BindCurrentChatFolder();
                var orchestrator = new AgentOrchestrator(new SystemClock(), new AgentLimits { MaxModelRounds = 24 });
                var result = await orchestrator.RunAsync(_history, true, this, new VisibleTools(this, _tools), AppendLive, _turnCancellation.Token).ConfigureAwait(true);
                _diagnostics.Write("turn_complete", phase: result.Status);
                FlushPendingText();
                PersistNewMessages();
                if (!string.IsNullOrEmpty(result.Detail))
                {
                    RejectUnavailableModel(result.Detail);
                    Items.Add(new TranscriptRow("Состояние", Explain(result.Detail), false));
                }

                BindCurrentChatFolder();
                RefreshLists();
            }
            catch (OperationCanceledException)
            {
                _diagnostics.Write("turn_cancelled");
                PreservePartialReply();
                Items.Add(new TranscriptRow("Остановлено", StringCatalog.Get("TurnStopped"), false));
                RefreshLists();
            }
            catch (Exception ex)
            {
                _diagnostics.Write("turn_failed", exception: ex, httpStatus: (ex as ApiCallException)?.StatusCode);
                PreservePartialReply();
                RejectUnavailableModel(ex.Message);
                Items.Add(new TranscriptRow("Ошибка", ApiErrorText.ForUser(ex.Message), false));
            }
            finally
            {
                _streamTimer.Stop();
                lock (_streamGate) _pendingText.Clear();
                _turnCancellation?.Dispose();
                _turnCancellation = null;
                _liveRow = null;
                _busy = false;
                PermissionVisible = false;
                SetActivity(string.Empty, false);
                RaiseAll();
                RefreshLists();
                _ = RefreshAccountAsync();
            }
        }

        private void RejectUnavailableModel(string message)
        {
            if (!ApiErrorText.IsHarnessRestriction(message)) return;
            var id = _selectedModel;
            _catalog.RemoveAll(model => model.Id == id);
            for (var i = Models.Count - 1; i >= 0; i--) if (Models[i].Id == id) Models.RemoveAt(i);
            _selectedCatalog = null;
            _selectedModel = string.Empty;
            _modelStatus = "SelectAnotherModel";
            Notify(nameof(SelectedCatalog));
            Notify(nameof(SelectedModel));
            Notify(nameof(ModelStatus));
        }

        private void PersistNewMessages()
        {
            foreach (var row in _streamRows) Items.Remove(row);
            _streamRows.Clear();
            _liveRow = null;

            var stored = _historyStore.Load(_chatId).Count;
            for (var i = stored; i < _history.Count; i++)
            {
                _historyStore.Append(_chatId, _history[i]);
                if (_history[i].Role == "tool")
                {
                    continue;
                }

                if (_history[i].Role != "user")
                {
                    AddRow(_history[i]);
                }
            }
        }

        private void PreservePartialReply()
        {
            FlushPendingText();
            if (_liveRow != null && _liveRow.Body.Length > 0)
            {
                var last = _history.Count > 0 ? _history[_history.Count - 1] : null;
                if (last == null || last.Role != "assistant" || last.Content != _liveRow.Body)
                {
                    _history.Add(new ChatMessage { Role = "assistant", Content = _liveRow.Body });
                }
            }

            try { PersistNewMessages(); }
            catch (IOException ex) { Items.Add(new TranscriptRow("История не сохранена", ex.Message, false)); }
            catch (UnauthorizedAccessException ex) { Items.Add(new TranscriptRow("История не сохранена", ex.Message, false)); }
        }

        private void EnsureSystem()
        {
            var text = SystemText();
            if (_history.Count == 0)
            {
                var message = new ChatMessage { Role = "system", Content = text };
                _history.Add(message);
                _historyStore.Append(_chatId, message);
                return;
            }

            if (_history[0].Role == "system")
            {
                if (_history[0].Content != text)
                {
                    _history[0].Content = text;
                    _historyStore.Rewrite(_chatId, _history);
                }

                return;
            }

            _history.Insert(0, new ChatMessage { Role = "system", Content = text });
            _historyStore.Rewrite(_chatId, _history);
        }

        private string SystemText()
        {
            var project = string.IsNullOrEmpty(_workspace.Root) ? "не выбрана" : _workspace.Root;
            return "Ты Arni Lite на Windows " + _os.Description + ".\n"
                + "Ты работаешь как агент. Если на вопрос можно ответить без инструментов, отвечай сразу. Для работы с файлами проекта нужна выбранная папка.\n"
                + "Рабочая папка проекта: " + project + ". Инструменты файлов видят только её.\n"
                + "Команды powershell и cmd выполняются от имени пользователя и могут читать другие папки, включая «Загрузки». Папка проекта их не ограничивает.\n"
                + "Доступна Windows PowerShell. Не обещай PowerShell 7, bash, Python, Git или Node, пока это не показал вывод команды.\n"
                + "Если пользователь дал точный текст команды, передай его в run_command без изменений. Для долгого хеширования ставь timeout_ms не меньше 300000.\n"
                + "Называй только числа из вывода инструмента. Если вывод обрезан, не читай сохранённый файл и повтори команду с более узким фильтром.\n"
                + "Не перемещай, не переименовывай, не удаляй и не распаковывай файлы, пока пользователь прямо не попросил это сделать.\n"
                + "Новый файл в проекте создавай через propose_file_change с must_not_exist=true. Не пиши, что файл создан, пока инструмент не вернул completed.\n"
                + "Если шаблон документа уже есть в сообщении пользователя, используй его и не ищи файл урока на диске.\n"
                + "Если своих данных для заполнения нет, оставь пометку «уточнить», не выдумывай биографию.\n"
                + "Когда пользователь просит остановиться после отчёта, остановись.\n"
                + "Если в корне проекта есть AGENTS.md, эти правила обязательны.\n"
                + ReadAgents()
                + "Для свободного места на диске и файлов вне проекта используй powershell. Системные папки Windows не анализируй. Ничего не удаляй и не перемещай в таком отчёте.";
        }

        private string ReadAgents()
        {
            if (string.IsNullOrEmpty(_workspace.Root))
            {
                return string.Empty;
            }

            var path = Path.Combine(_workspace.Root, "AGENTS.md");
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var text = File.ReadAllText(path);
            if (text.Length > 6000)
            {
                text = text.Substring(0, 6000);
            }

            return "AGENTS.md:\n" + text.Trim() + "\n";
        }

        private void AppendLive(string text)
        {
            lock (_streamGate) _pendingText.Append(text);
        }

        private void FlushPendingText()
        {
            string text;
            lock (_streamGate)
            {
                if (_pendingText.Length == 0) return;
                text = _pendingText.ToString();
                _pendingText.Clear();
            }

            if (_liveRow == null)
            {
                _liveRow = new TranscriptRow("Arni", string.Empty, false);
                _streamRows.Add(_liveRow);
                Items.Add(_liveRow);
            }

            _liveRow.Append(text);
            SetActivity(StringCatalog.Get("Streaming"), true);
            TextGrew?.Invoke();
        }

        public void Stop()
        {
            _turnCancellation?.Cancel();
        }

        private void AddRow(ChatMessage message)
        {
            if (message.Role == "system")
            {
                return;
            }

            if (message.Role == "tool")
            {
                Items.Add(new TranscriptRow("Результат", PresentTool(message.Content), true));
                return;
            }

            var title = message.Role == "user" ? "Вы" : "Arni";
            var parts = CodeFences.Split(message.Content);
            if (message.ToolCalls != null)
            {
                for (var i = 0; i < message.ToolCalls.Count; i++)
                {
                    var call = message.ToolCalls[i];
                    Items.Add(new TranscriptRow(call.Name, call.ArgumentsJson, true));
                }
            }

            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i].Text.Length == 0)
                {
                    continue;
                }

                Items.Add(new TranscriptRow(title, parts[i].Text, parts[i].IsCode));
            }
        }

        private void NewChat()
        {
            if (_busy)
            {
                return;
            }

            _workspace.ClearRoot();
            StartChat();
        }

        private void StartChat()
        {
            _chatId = _historyStore.NewChat();
            BindCurrentChatFolder();
            Draft = string.Empty;
            LoadCurrentChat();
            RefreshLists();
        }

        private void NewProjectChat(object parameter)
        {
            if (_busy) return;
            var path = parameter as string;
            if (string.IsNullOrEmpty(path)) { NewChat(); return; }
            try
            {
                _workspace.SetRoot(path);
                _collapsedProjects.Remove(path);
                StartChat();
            }
            catch (Exception ex) { Items.Add(new TranscriptRow("Проект", ex.Message, false)); }
        }

        private void ToggleProject(object parameter)
        {
            var path = parameter as string ?? string.Empty;
            if (!_collapsedProjects.Add(path)) _collapsedProjects.Remove(path);
            RebuildChatRows();
        }

        private void OpenChat(object parameter)
        {
            var id = parameter as string;
            if (_busy || string.IsNullOrEmpty(id) || id == _chatId)
            {
                return;
            }

            _chatId = id;
            File.WriteAllText(Path.Combine(_dataDirectory, "chats", "current.txt"), id);
            LoadCurrentChat();
            RebuildChatRows();
        }

        private void OpenFolder(object parameter)
        {
            if (_busy) return;
            var path = parameter as string;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                var map = ReadChatFolders();
                foreach (var chat in Chats)
                {
                    if (map.TryGetValue(chat.Id, out var folder) && string.Equals(folder, path, StringComparison.OrdinalIgnoreCase))
                    {
                        OpenChat(chat.Id);
                        return;
                    }
                }

                NewProjectChat(path);
            }
            catch (Exception ex)
            {
                Items.Add(new TranscriptRow("Папка", ex.Message, false));
            }
        }

        private void LoadCurrentChat()
        {
            _history.Clear();
            _streamRows.Clear();
            Items.Clear();
            _liveRow = null;
            Preview = string.Empty;
            ClosePreview();
            _workspace.ClearRoot();
            var map = ReadChatFolders();
            if (map.TryGetValue(_chatId, out var folder))
            {
                try { _workspace.SetRoot(folder); }
                catch (Exception) { Items.Add(new TranscriptRow("Проект недоступен", StringCatalog.Get("ProjectUnavailableDetail", folder), false)); }
            }

            ProjectLabel = HasProject ? "Проект: " + _workspace.Root : "Чат без доступа к проекту";
            Notify(nameof(ProjectPath));
            Notify(nameof(ProjectName));
            Notify(nameof(HasProject));
            Notify(nameof(ChatTitle));
            RefreshProjectFiles();
            foreach (var message in _historyStore.Load(_chatId))
            {
                _history.Add(message);
                AddRow(message);
            }
            Notify(nameof(ChatTitle));
            (OpenInExplorerCommand as RelayCommand)?.Raise();
        }

        private void RefreshLists()
        {
            Chats.Clear();
            foreach (var chat in _historyStore.List())
            {
                Chats.Add(chat);
            }
            var hasCurrent = false;
            foreach (var chat in Chats) if (chat.Id == _chatId) hasCurrent = true;
            if (!hasCurrent) Chats.Insert(0, new ChatSummary { Id = _chatId, Title = StringCatalog.Get("NewChat"), Updated = DateTime.Now });

            Folders.Clear();
            var folderFile = Path.Combine(_dataDirectory, "folders.txt");
            if (File.Exists(folderFile))
            {
                foreach (var line in File.ReadAllLines(folderFile))
                {
                    if (Directory.Exists(line))
                    {
                        Folders.Add(line);
                    }
                }
            }

            RebuildChatRows();
        }

        private void RebuildChatRows()
        {
            var foldersByChat = ReadChatFolders();
            var groups = new Dictionary<string, List<ChatSummary>>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Folders) groups[folder] = new List<ChatSummary>();
            foreach (var chat in Chats)
            {
                var folder = string.Empty;
                if (foldersByChat.ContainsKey(chat.Id))
                {
                    folder = foldersByChat[chat.Id];
                }

                if (!groups.ContainsKey(folder))
                {
                    groups[folder] = new List<ChatSummary>();
                }

                groups[folder].Add(chat);
            }

            var keys = new List<string>(groups.Keys);
            keys.Sort((left, right) =>
            {
                if (left.Length == 0) return 1;
                if (right.Length == 0) return -1;
                return string.Compare(Path.GetFileName(left), Path.GetFileName(right), StringComparison.CurrentCultureIgnoreCase);
            });
            ChatRows.Clear();
            foreach (var key in keys)
            {
                var visible = new List<ChatSummary>();
                var projectMatches = key.IndexOf(_search, StringComparison.CurrentCultureIgnoreCase) >= 0;
                foreach (var chat in groups[key])
                {
                    if (projectMatches || chat.Title.IndexOf(_search, StringComparison.CurrentCultureIgnoreCase) >= 0) visible.Add(chat);
                }
                if (_search.Length > 0 && visible.Count == 0 && !projectMatches) continue;

                ChatRows.Add(new SideEntry
                {
                    IsHeader = true,
                    Title = key.Length == 0 ? StringCatalog.Get("GeneralChats") : Path.GetFileName(key.TrimEnd('\\')),
                    ProjectPath = key,
                    IsExpanded = _search.Length > 0 || !_collapsedProjects.Contains(key),
                });
                if (_search.Length == 0 && _collapsedProjects.Contains(key)) continue;
                foreach (var chat in visible)
                {
                    ChatRows.Add(new SideEntry { Title = chat.Title, ChatId = chat.Id, ProjectPath = key });
                }
            }
            _selectedEntry = null;
            foreach (var row in ChatRows) if (row.ChatId == _chatId) _selectedEntry = row;
            Notify(nameof(SelectedEntry));
        }

        private void BindCurrentChatFolder()
        {
            if (string.IsNullOrEmpty(_chatId) || string.IsNullOrEmpty(_workspace.Root))
            {
                return;
            }

            var map = ReadChatFolders();
            map[_chatId] = _workspace.Root;
            var lines = new List<string>();
            foreach (var pair in map)
            {
                lines.Add(pair.Key + "\t" + pair.Value);
            }

            Directory.CreateDirectory(_dataDirectory);
            File.WriteAllLines(Path.Combine(_dataDirectory, "chat-folders.txt"), lines);
        }

        private Dictionary<string, string> ReadChatFolders()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(_dataDirectory, "chat-folders.txt");
            if (!File.Exists(path))
            {
                return map;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                var split = line.IndexOf('\t');
                if (split > 0)
                {
                    map[line.Substring(0, split)] = line.Substring(split + 1);
                }
            }

            return map;
        }

        private void RefreshProjectFiles()
        {
            ProjectFiles.Clear();
            if (string.IsNullOrEmpty(_workspace.Root) || !Directory.Exists(_workspace.Root))
            {
                return;
            }

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(_workspace.Root))
                {
                    if (ProjectFiles.Count >= 80) break;

                    var name = Path.GetFileName(entry);
                    ProjectFiles.Add(Directory.Exists(entry) ? name + "\\" : name);
                }
            }
            catch (IOException ex)
            {
                Items.Add(new TranscriptRow("Файлы недоступны", ex.Message, false));
            }
            catch (UnauthorizedAccessException ex)
            {
                Items.Add(new TranscriptRow("Нет доступа к файлам", ex.Message, false));
            }
        }

        private void OpenInExplorer()
        {
            if (string.IsNullOrEmpty(_workspace.Root))
            {
                return;
            }

            System.Diagnostics.Process.Start("explorer.exe", "\"" + _workspace.Root + "\"");
        }

        private void OpenProjectFile(object parameter)
        {
            var label = parameter as string;
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrEmpty(_workspace.Root))
            {
                return;
            }

            var name = label.Trim().TrimEnd('\\');
            if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            {
                return;
            }

            if (!WorkspacePath.TryCombine(_workspace.Root, name, out var full, out var error))
            {
                Items.Add(new TranscriptRow("Файл", error, false));
                return;
            }

            if (!File.Exists(full) && !Directory.Exists(full))
            {
                return;
            }

            try
            {
                _filePreviewTitle = name;
                _filePreviewPath = string.Empty;
                _previewKind = PreviewKind.Text;
                _filePreviewBody = string.Empty;
                if (Directory.Exists(full)) _filePreviewBody = StringCatalog.Get("FolderPreviewHelp");
                else
                {
                    full = _workspace.ResolveFile(name);
                    _previewKind = PreviewPolicy.Classify(full);
                    if (_previewKind != PreviewKind.Text) _filePreviewPath = full;
                    else _filePreviewBody = new FileInfo(full).Length > 1024 * 1024
                        ? StringCatalog.Get("FileTooLarge") : _workspace.Read(name, 1, 500).Text;
                }
                _diagnostics.Write("preview_open", phase: _previewKind.ToString());
            }
            catch (Exception ex)
            {
                _previewKind = PreviewKind.Text;
                _filePreviewPath = string.Empty;
                _filePreviewBody = StringCatalog.Get("PreviewFailed", ex.Message);
                _diagnostics.Write("preview_failed", exception: ex);
            }
            Notify(nameof(FilePreviewTitle));
            Notify(nameof(FilePreviewBody));
            Notify(nameof(HasFilePreview));
            NotifyPreview();
        }

        private void RememberFolder(string path)
        {
            var folderFile = Path.Combine(_dataDirectory, "folders.txt");
            var lines = File.Exists(folderFile) ? File.ReadAllLines(folderFile) : new string[0];
            var kept = new List<string> { path };
            for (var i = 0; i < lines.Length && kept.Count < 8; i++)
            {
                if (!string.Equals(lines[i], path, StringComparison.OrdinalIgnoreCase) && Directory.Exists(lines[i]))
                {
                    kept.Add(lines[i]);
                }
            }

            Directory.CreateDirectory(_dataDirectory);
            File.WriteAllLines(folderFile, kept);
            RefreshLists();
        }

        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = StringCatalog.Get("FolderDialog") })
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    RememberFolder(dialog.SelectedPath);
                    NewProjectChat(dialog.SelectedPath);
                }
                catch (Exception ex)
                {
                    Items.Add(new TranscriptRow("Папка", ex.Message, false));
                }
            }
        }

        private void Resolve(bool allowed)
        {
            PermissionVisible = false;
            if (allowed)
            {
                SetActivity(StringCatalog.Get("AllowedRunning"), true);
            }

            _approval?.TrySetResult(allowed);
            _approval = null;
            RaiseAll();
        }

        private void SetActivity(string text, bool loader)
        {
            Activity = text ?? string.Empty;
            ShowLoader = loader && Activity.Length > 0;
            Notify(nameof(HasActivity));
        }

        private static string Explain(string detail)
        {
            if (ApiErrorText.IsHarnessRestriction(detail)) return ApiErrorText.ForUser(detail);
            if (string.IsNullOrEmpty(detail))
            {
                return string.Empty;
            }

            if (detail.IndexOf("model-round", StringComparison.Ordinal) >= 0)
            {
                return StringCatalog.Get("RoundLimit");
            }

            if (detail.IndexOf("time limit", StringComparison.Ordinal) >= 0)
            {
                return StringCatalog.Get("TimeLimit");
            }

            if (detail.IndexOf("tool-call", StringComparison.Ordinal) >= 0)
            {
                return StringCatalog.Get("ToolLimit");
            }

            return detail;
        }

        private static string PresentTool(string json)
        {
            try
            {
                var body = JObject.Parse(json);
                var result = body["result"] as JObject;
                if (result == null)
                {
                    return json;
                }

                if (result["stdout"] != null || result["stderr"] != null)
                {
                    var text = "Код выхода: " + (result["exit_code"] == null ? "нет" : result["exit_code"].ToString())
                        + ", " + result["duration_ms"] + " мс";
                    var stdout = (string)result["stdout"] ?? string.Empty;
                    var stderr = (string)result["stderr"] ?? string.Empty;
                    if (stdout.Length > 0)
                    {
                        text += "\n" + stdout.Trim();
                    }

                    if (stderr.Length > 0)
                    {
                        text += "\n" + stderr.Trim();
                    }

                    return text;
                }

                return result.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception)
            {
                return json;
            }
        }

        private void ShowTool(ToolCallRecord call, ToolExecution result)
        {
            FlushPendingText();
            _liveRow = null;
            SetActivity(result.Status == "completed" ? StringCatalog.Get("ToolDone") : StringCatalog.Get("ToolFinished", result.Status), true);
            Items.Add(new TranscriptRow(call.Name, PresentTool(result.ContentJson), true));
            RefreshProjectFiles();
            TextGrew?.Invoke();
        }

        private sealed class VisibleTools : IToolExecutor
        {
            private readonly ChatSession _session;
            private readonly IToolExecutor _inner;

            public VisibleTools(ChatSession session, IToolExecutor inner)
            {
                _session = session;
                _inner = inner;
            }

            public async Task<ToolExecution> ExecuteAsync(string turnId, int round, ToolCallRecord call, CancellationToken cancellationToken)
            {
                _session.Post(() => _session.SetActivity(StringCatalog.Get("ToolRunning", call.Name), true));
                var result = await _inner.ExecuteAsync(turnId, round, call, cancellationToken).ConfigureAwait(false);
                _session.Post(() => _session.ShowTool(call, result));
                return result;
            }
        }

        private void Post(Action action)
        {
            if (_ui == null || SynchronizationContext.Current == _ui)
            {
                action();
            }
            else
            {
                _ui.Post(_ => action(), null);
            }
        }

        private void RaiseAll()
        {
            Notify(nameof(IsBusy));
            Notify(nameof(CanChangeContext));
            (RefreshAccountCommand as RelayCommand)?.Raise();
            (StopCommand as RelayCommand)?.Raise();
            (SendCommand as RelayCommand)?.Raise();
            (NewChatCommand as RelayCommand)?.Raise();
            (OpenInExplorerCommand as RelayCommand)?.Raise();
            (ChooseFolderCommand as RelayCommand)?.Raise();
            (AllowCommand as RelayCommand)?.Raise();
            (DenyCommand as RelayCommand)?.Raise();
        }
    }

    public sealed class TranscriptRow : ViewModelBase
    {
        private string _body;

        public TranscriptRow(string title, string body, bool code)
        {
            Title = title ?? string.Empty;
            _body = body ?? string.Empty;
            IsCode = code;
            CopyCommand = new RelayCommand(() => System.Windows.Clipboard.SetText(Body));
        }

        public string Title { get; }
        public string DisplayTitle => Title switch
        {
            "search_text" => StringCatalog.Get("SearchProject"),
            "read_file" => StringCatalog.Get("ReadFile"),
            "list_files" => StringCatalog.Get("ListFiles"),
            "run_command" => StringCatalog.Get("RunCommand"),
            "propose_file_change" => StringCatalog.Get("ChangeFile"),
            "Вы" => StringCatalog.Get("You"),
            "Результат" => StringCatalog.Get("Result"),
            "Ошибка" => StringCatalog.Get("Error"),
            "Состояние" => StringCatalog.Get("Status"),
            "Остановлено" => StringCatalog.Get("Stopped"),
            "Проект" => StringCatalog.Get("Project"),
            "Файл" => StringCatalog.Get("File"),
            "История не сохранена" => StringCatalog.Get("HistoryNotSaved"),
            "Проект недоступен" => StringCatalog.Get("ProjectUnavailable"),
            "Файлы недоступны" => StringCatalog.Get("FilesUnavailable"),
            "Нет доступа к файлам" => StringCatalog.Get("NoFileAccess"),
            _ => Title,
        };
        public void RefreshLocalization() => Notify(nameof(DisplayTitle));
        public bool IsCode { get; }
        public bool IsTool => IsCode && Title != "Arni" && Title != "Вы";
        public bool IsCodeBlock => IsCode && !IsTool;
        public bool IsUser => Title == "Вы";
        public bool PreferMono => IsCode || !IsUser;
        public ICommand CopyCommand { get; }

        public string Body
        {
            get => _body;
            private set { _body = value; Notify(nameof(Body)); }
        }

        public void Append(string text)
        {
            Body += text;
        }
    }

    public sealed class SideEntry
    {
        public string Title { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
        public bool IsHeader { get; set; }
        public string ProjectPath { get; set; } = string.Empty;
        public bool IsExpanded { get; set; }
        public string Disclosure => IsExpanded ? "−" : "+";
        public bool CanOpen => !IsHeader && !string.IsNullOrEmpty(ChatId);
    }

    public sealed class ParamCommand : ICommand
    {
        private readonly Action<object> _execute;

        public ParamCommand(Action<object> execute)
        {
            _execute = execute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            return true;
        }

        public void Execute(object parameter)
        {
            _execute(parameter);
        }
    }
}
