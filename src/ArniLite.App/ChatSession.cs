using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
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
        private TaskCompletionSource<bool> _approval;
        private TranscriptRow _liveRow;
        private string _chatId;
        private string _draft = string.Empty;
        private string _projectLabel = "Проекты сохраняются в: не выбрано";
        private string _preview = string.Empty;
        private string _accountLine = string.Empty;
        private string _accountEmail = string.Empty;
        private string _accountProvider = string.Empty;
        private string _selectedMode = "Агент";
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

        public ChatSession(ArniApiClient api, ITokenStore tokens, OsFacts os, IProcessRunner processes)
        {
            _api = api;
            _tokens = tokens;
            _os = os;
            _ui = SynchronizationContext.Current;
            _journal = JsonlJournal.InDataDirectory();
            _journal.MarkExecutingAsUnknown();
            _historyStore = ChatHistory.InDataDirectory();
            _tools = new LocalToolHost(_workspace, processes, _journal, this);
            _chatId = _historyStore.CurrentChatId();
            LoadCurrentChat();
            RefreshLists();
            RestoreLastFolder();

            Modes.Add("Чат");
            Modes.Add("Агент");
            OpenChatCommand = new ParamCommand(OpenChat);
            OpenFolderCommand = new ParamCommand(OpenFolder);
            OpenInExplorerCommand = new RelayCommand(OpenInExplorer, () => !string.IsNullOrEmpty(_workspace.Root));
            OpenProjectFileCommand = new ParamCommand(OpenProjectFile);
            SendCommand = new RelayCommand(async () => await SendAsync(), () => !_busy && !string.IsNullOrWhiteSpace(Draft));
            NewChatCommand = new RelayCommand(NewChat, () => !_busy);
            ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !_busy);
            AllowCommand = new RelayCommand(() => Resolve(true), () => _permissionVisible);
            DenyCommand = new RelayCommand(() => Resolve(false), () => _permissionVisible);
        }

        public ObservableCollection<TranscriptRow> Items { get; } = new ObservableCollection<TranscriptRow>();
        public ObservableCollection<string> Modes { get; } = new ObservableCollection<string>();
        public ObservableCollection<CatalogModel> Models { get; } = new ObservableCollection<CatalogModel>();
        public ObservableCollection<ChatSummary> Chats { get; } = new ObservableCollection<ChatSummary>();
        public ObservableCollection<string> Folders { get; } = new ObservableCollection<string>();
        public ObservableCollection<SideEntry> ChatRows { get; } = new ObservableCollection<SideEntry>();
        public ObservableCollection<string> ProjectFiles { get; } = new ObservableCollection<string>();
        public ICommand OpenInExplorerCommand { get; }
        public ICommand OpenProjectFileCommand { get; }
        public ICommand OpenChatCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand SendCommand { get; }
        public ICommand NewChatCommand { get; }
        public ICommand ChooseFolderCommand { get; }
        public ICommand AllowCommand { get; }
        public ICommand DenyCommand { get; }
        public bool AllowSideEffectsForSession => _trust;

        public string Draft
        {
            get => _draft;
            set { _draft = value; Notify(nameof(Draft)); (SendCommand as RelayCommand)?.Raise(); }
        }

        public string SelectedMode
        {
            get => _selectedMode;
            set { _selectedMode = value; Notify(nameof(SelectedMode)); }
        }

        public CatalogModel SelectedCatalog
        {
            get => _selectedCatalog;
            set
            {
                _selectedCatalog = value;
                if (value != null)
                {
                    _selectedModel = value.Id;
                }

                Notify(nameof(SelectedCatalog));
                Notify(nameof(SelectedModel));
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

        public string AccountLine
        {
            get => _accountLine;
            private set { _accountLine = value; Notify(nameof(AccountLine)); }
        }

        public string AccountEmail
        {
            get => _accountEmail;
            private set { _accountEmail = value ?? string.Empty; Notify(nameof(AccountEmail)); }
        }

        public string AccountProvider
        {
            get => _accountProvider;
            private set
            {
                _accountProvider = value ?? string.Empty;
                Notify(nameof(AccountProvider));
                Notify(nameof(IsYandex));
                Notify(nameof(IsGoogle));
                Notify(nameof(IsVk));
                Notify(nameof(IsOtherProvider));
            }
        }

        public bool IsYandex => _accountProvider == "yandex";
        public bool IsGoogle => _accountProvider == "google";
        public bool IsVk => _accountProvider == "vk";
        public bool IsOtherProvider => _accountProvider != "yandex" && _accountProvider != "google" && _accountProvider != "vk";

        public void ClearAccount()
        {
            AccountEmail = string.Empty;
            AccountProvider = string.Empty;
            AccountLine = string.Empty;
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

            try
            {
                var models = await _api.ListModelsAsync(token, CancellationToken.None).ConfigureAwait(true);
                _catalog.Clear();
                _catalog.AddRange(models);
                ApplyModelFilter();

                var account = await _api.AccountAsync(token, CancellationToken.None).ConfigureAwait(true);
                AccountEmail = account.Email;
                AccountProvider = account.Provider;
                AccountLine = account.Summary;
            }
            catch (Exception ex)
            {
                AccountLine = ex.Message;
            }
        }

        public async Task<ModelTurn> CompleteAsync(IList<ChatMessage> messages, bool withTools, Action<string> onDelta, CancellationToken cancellationToken)
        {
            var token = _tokens.Load();
            if (string.IsNullOrEmpty(token))
            {
                return new ModelTurn { Error = "Sign in first.", Done = true, FinishReason = "error" };
            }

            Post(() => SetActivity("Модель думает…", true));
            var model = string.IsNullOrEmpty(_selectedModel) ? (_selectedCatalog == null ? string.Empty : _selectedCatalog.Id) : _selectedModel;
            if (string.IsNullOrEmpty(model))
            {
                return new ModelTurn { Error = "Выберите модель.", Done = true, FinishReason = "error" };
            }

            return await _api.CompleteAsync(token, model, "arni-lite:" + _chatId, messages, withTools, onDelta, cancellationToken).ConfigureAwait(true);
        }

        public Task<bool> ApproveAsync(string title, string body, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<bool>();
            Post(() =>
            {
                SetActivity("Жду подтверждения…", false);
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
            SetActivity("Модель думает…", true);
            Draft = string.Empty;
            RaiseAll();
            try
            {
                EnsureSystem();
                var user = new ChatMessage { Role = "user", Content = text };
                _history.Add(user);
                _historyStore.Append(_chatId, user);
                AddRow(user);
                var orchestrator = new AgentOrchestrator(new SystemClock(), new AgentLimits { MaxModelRounds = 24 });
                var result = await orchestrator.RunAsync(_history, NeedsTools(), this, new VisibleTools(this, _tools), AppendLive, CancellationToken.None).ConfigureAwait(true);
                PersistNewMessages();
                if (!string.IsNullOrEmpty(result.Detail))
                {
                    Items.Add(new TranscriptRow("Состояние", Explain(result.Detail), false));
                }

                RefreshLists();
                BindCurrentChatFolder();
            }
            catch (Exception ex)
            {
                Items.Add(new TranscriptRow("Ошибка", ex.Message, false));
            }
            finally
            {
                _liveRow = null;
                _busy = false;
                PermissionVisible = false;
                SetActivity(string.Empty, false);
                RaiseAll();
            }
        }

        private void PersistNewMessages()
        {
            if (_liveRow != null)
            {
                Items.Remove(_liveRow);
                _liveRow = null;
            }

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

        private bool NeedsTools()
        {
            if (SelectedMode == "Агент")
            {
                return true;
            }

            for (var i = 0; i < _history.Count; i++)
            {
                if (_history[i].Role == "tool" || (_history[i].ToolCalls != null && _history[i].ToolCalls.Count > 0))
                {
                    return true;
                }
            }

            return false;
        }

        private void AppendLive(string text)
        {
            Post(() =>
            {
                if (_liveRow == null)
                {
                    _liveRow = new TranscriptRow("Arni", string.Empty, false);
                    Items.Add(_liveRow);
                }

                _liveRow.Append(text);
                SetActivity("Ответ поступает…", true);
                TextGrew?.Invoke();
            });
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

            _chatId = _historyStore.NewChat();
            LoadCurrentChat();
            RefreshLists();
        }

        private void OpenChat(object parameter)
        {
            var id = parameter as string;
            if (_busy || string.IsNullOrEmpty(id) || id == _chatId)
            {
                return;
            }

            _chatId = id;
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "chats", "current.txt"), id);
            LoadCurrentChat();
        }

        private void OpenFolder(object parameter)
        {
            var path = parameter as string;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                _workspace.SetRoot(path);
                ProjectLabel = "Проекты сохраняются в: " + _workspace.Root;
                RememberFolder(path);
                BindCurrentChatFolder();
            }
            catch (Exception ex)
            {
                Items.Add(new TranscriptRow("Папка", ex.Message, false));
            }
        }

        private void LoadCurrentChat()
        {
            _history.Clear();
            Items.Clear();
            _liveRow = null;
            Preview = string.Empty;
            foreach (var message in _historyStore.Load(_chatId))
            {
                _history.Add(message);
                AddRow(message);
            }
        }

        private void RefreshLists()
        {
            Chats.Clear();
            foreach (var chat in _historyStore.List())
            {
                Chats.Add(chat);
            }

            Folders.Clear();
            var folderFile = Path.Combine(AppPaths.DataDirectory, "folders.txt");
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

            var foldersByChat = ReadChatFolders();
            var groups = new Dictionary<string, List<ChatSummary>>(StringComparer.OrdinalIgnoreCase);
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
                ChatRows.Add(new SideEntry
                {
                    IsHeader = true,
                    Title = key.Length == 0 ? "Без папки" : Path.GetFileName(key.TrimEnd('\\')),
                });
                foreach (var chat in groups[key])
                {
                    ChatRows.Add(new SideEntry { Title = chat.Title, ChatId = chat.Id });
                }
            }

            RefreshProjectFiles();
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

            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllLines(Path.Combine(AppPaths.DataDirectory, "chat-folders.txt"), lines);
        }

        private Dictionary<string, string> ReadChatFolders()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(AppPaths.DataDirectory, "chat-folders.txt");
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

            foreach (var entry in Directory.GetFileSystemEntries(_workspace.Root))
            {
                if (ProjectFiles.Count >= 80)
                {
                    break;
                }

                var name = Path.GetFileName(entry);
                ProjectFiles.Add(Directory.Exists(entry) ? name + "\\" : name);
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
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = full,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Items.Add(new TranscriptRow("Файл", ex.Message, false));
            }
        }

        private void RestoreLastFolder()
        {
            if (!string.IsNullOrEmpty(_workspace.Root) || Folders.Count == 0)
            {
                return;
            }

            try
            {
                _workspace.SetRoot(Folders[0]);
                ProjectLabel = "Проекты сохраняются в: " + _workspace.Root;
                RefreshProjectFiles();
            }
            catch (Exception)
            {
            }
        }

        private void RememberFolder(string path)
        {
            var folderFile = Path.Combine(AppPaths.DataDirectory, "folders.txt");
            var lines = File.Exists(folderFile) ? File.ReadAllLines(folderFile) : new string[0];
            var kept = new List<string> { path };
            for (var i = 0; i < lines.Length && kept.Count < 8; i++)
            {
                if (!string.Equals(lines[i], path, StringComparison.OrdinalIgnoreCase) && Directory.Exists(lines[i]))
                {
                    kept.Add(lines[i]);
                }
            }

            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllLines(folderFile, kept);
            RefreshLists();
        }

        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Папка проекта для режима «Агент»" })
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    _workspace.SetRoot(dialog.SelectedPath);
                    ProjectLabel = "Проекты сохраняются в: " + _workspace.Root;
                    RememberFolder(dialog.SelectedPath);
                    BindCurrentChatFolder();
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
                SetActivity("Разрешено. Выполняю…", true);
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
            if (string.IsNullOrEmpty(detail))
            {
                return string.Empty;
            }

            if (detail.IndexOf("model-round", StringComparison.Ordinal) >= 0)
            {
                return "Ход остановлен: лимит шагов модели. Отчёт мог остаться незаконченным.";
            }

            if (detail.IndexOf("time limit", StringComparison.Ordinal) >= 0)
            {
                return "Ход остановлен по времени.";
            }

            if (detail.IndexOf("tool-call", StringComparison.Ordinal) >= 0)
            {
                return "Ход остановлен: слишком много вызовов инструментов.";
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
            SetActivity(result.Status == "completed" ? "Готово, модель читает результат…" : "Инструмент завершился: " + result.Status, true);
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
                _session.Post(() => _session.SetActivity("Выполняю " + call.Name + "…", true));
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
        public bool IsCode { get; }
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
