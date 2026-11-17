using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Storage;
using ArniLite.Windows;

namespace ArniLite
{
    public sealed class LanguageChoice
    {
        public string Code { get; }
        public string Name { get; }
        public LanguageChoice(string code, string name) { Code = code; Name = name; }
    }

    public sealed class McpServerItem : ViewModelBase
    {
        private readonly PreferencesViewModel _owner;
        private bool _connected;
        public McpServerItem(PreferencesViewModel owner, string id, string host, string url, bool connected)
        {
            _owner = owner;
            Id = id ?? string.Empty;
            Host = host ?? string.Empty;
            Url = url ?? string.Empty;
            _connected = connected;
            RemoveCommand = new RelayCommand(() => _owner.Remove(Id), () => _owner.CanChangeMcp);
        }

        public string Id { get; }
        public string Host { get; }
        public string Url { get; }
        public bool Connected
        {
            get => _connected;
            set
            {
                _connected = value;
                Notify(nameof(Connected));
                Notify(nameof(StatusTip));
            }
        }
        public string StatusTip => StringCatalog.Get(_connected ? "McpConnectedTip" : "McpDisconnectedTip");
        public ICommand RemoveCommand { get; }
        public void RefreshTip() => Notify(nameof(StatusTip));
        public void Raise() => (RemoveCommand as RelayCommand)?.Raise();
    }

    public sealed class PreferencesViewModel : ViewModelBase
    {
        private readonly PreferencesStore _store;
        private readonly McpSecretCatalog _secrets;
        private readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _saveFailed;
        private bool _mcpBusy;
        private string _mcpMessageKey = string.Empty;
        public PreferencesStore Store => _store;
        public ObservableCollection<McpServerItem> Servers { get; } = new ObservableCollection<McpServerItem>();

        public PreferencesViewModel(PreferencesStore store, McpSecretCatalog secrets = null)
        {
            _store = store;
            _secrets = secrets;
            AdoptLegacy();
            var servers = _store.Value.McpServers ?? new List<McpServerRecord>();
            for (var i = 0; i < servers.Count; i++)
            {
                var server = servers[i];
                Servers.Add(new McpServerItem(this, server.Id, McpAddress.Host(server.Url), server.Url, server.Connected));
            }
        }

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
                for (var i = 0; i < Servers.Count; i++) Servers[i].RefreshTip();
                Notify(nameof(McpMessage));
                Save();
            }
        }
        public bool WrapText
        {
            get => _store.Value.WrapText;
            set { _store.Value.WrapText = value; Notify(nameof(WrapText)); Save(); }
        }
        public string SaveError => _saveFailed ? StringCatalog.Get("PreferencesFailed") : string.Empty;
        public string McpMessage => _mcpMessageKey.Length == 0 ? string.Empty : StringCatalog.Get(_mcpMessageKey);
        public bool CanChangeMcp => !_mcpBusy;

        public async Task RefreshAsync()
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(true))
            {
                return;
            }

            SetBusy(true);
            try
            {
                var servers = _store.Value.McpServers;
                for (var i = 0; i < servers.Count; i++)
                {
                    var server = servers[i];
                    var connected = await ProbeAsync(server.Url, _secrets?.Load(server.Id)).ConfigureAwait(true);
                    if (Find(server.Id) == null)
                    {
                        continue;
                    }

                    server.Connected = connected;
                    var row = FindRow(server.Id);
                    if (row != null)
                    {
                        row.Connected = connected;
                    }
                }

                Save();
            }
            finally
            {
                SetBusy(false);
                _gate.Release();
            }
        }

        public async Task<bool> AddAsync(string rawUrl, string token)
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            SetBusy(true);
            try
            {
                var normalized = McpAddress.Normalize(rawUrl);
                if (normalized.Length == 0)
                {
                    return Message("McpUrlInvalid", false);
                }

                if (Contains(normalized))
                {
                    return Message("McpDuplicate", false);
                }

                if (_store.Value.McpServers.Count >= McpAddress.MaxServers)
                {
                    return Message("McpLimit", false);
                }

                _mcpMessageKey = "McpChecking";
                Notify(nameof(McpMessage));
                var connected = await ProbeAsync(normalized, token).ConfigureAwait(true);
                var id = Guid.NewGuid().ToString("N");
                try
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        _secrets?.Save(id, token);
                    }
                }
                catch (Exception ex)
                {
                    _saveFailed = true;
                    DiagnosticLog.Current.Write("mcp_auth_save_failed", exception: ex);
                    Notify(nameof(SaveError));
                    return Message(string.Empty, false);
                }

                _store.Value.McpServers.Add(new McpServerRecord { Id = id, Url = normalized, Connected = connected });
                if (!Save())
                {
                    _store.Value.McpServers.RemoveAt(_store.Value.McpServers.Count - 1);
                    try { _secrets?.Remove(id); } catch (Exception) { }
                    return Message(string.Empty, false);
                }

                Servers.Add(new McpServerItem(this, id, McpAddress.Host(normalized), normalized, connected));
                return Message(string.Empty, true);
            }
            finally
            {
                SetBusy(false);
                _gate.Release();
            }
        }

        public void Remove(string id)
        {
            if (_mcpBusy || string.IsNullOrEmpty(id))
            {
                return;
            }

            var servers = _store.Value.McpServers;
            for (var i = servers.Count - 1; i >= 0; i--)
            {
                if (servers[i].Id == id)
                {
                    servers.RemoveAt(i);
                }
            }

            for (var i = Servers.Count - 1; i >= 0; i--)
            {
                if (Servers[i].Id == id)
                {
                    Servers.RemoveAt(i);
                }
            }

            try
            {
                _secrets?.Remove(id);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _saveFailed = true;
                DiagnosticLog.Current.Write("mcp_auth_clear_failed", exception: ex);
            }

            Save();
            Notify(nameof(SaveError));
        }

        public IReadOnlyList<McpEndpoint> Endpoints()
        {
            var list = new List<McpEndpoint>();
            var servers = _store.Value.McpServers ?? new List<McpServerRecord>();
            for (var i = 0; i < servers.Count; i++)
            {
                var server = servers[i];
                if (server == null || string.IsNullOrWhiteSpace(server.Url))
                {
                    continue;
                }

                list.Add(new McpEndpoint
                {
                    Url = server.Url,
                    Authorization = _secrets == null ? string.Empty : _secrets.Load(server.Id),
                });
            }

            return list;
        }

        private async Task<bool> ProbeAsync(string url, string token)
        {
            McpSession session = null;
            try
            {
                session = await McpClient.OpenAsync(_http, url, token ?? string.Empty, CancellationToken.None).ConfigureAwait(true);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Current.Write("mcp_probe_failed", exception: ex);
                return false;
            }
            finally
            {
                if (session != null)
                {
                    await session.CloseAsync().ConfigureAwait(true);
                }
            }
        }

        private void AdoptLegacy()
        {
            if (_secrets == null || !_secrets.HasLegacy)
            {
                return;
            }

            var id = _store.Value.LegacySecretId ?? string.Empty;
            if (id.Length == 0 && _store.Value.McpServers.Count == 1)
            {
                id = _store.Value.McpServers[0].Id;
            }

            if (id.Length == 0)
            {
                return;
            }

            try
            {
                _secrets.AdoptLegacy(id);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Current.Write("mcp_auth_save_failed", exception: ex);
            }
        }

        private bool Message(string key, bool added)
        {
            _mcpMessageKey = key ?? string.Empty;
            Notify(nameof(McpMessage));
            return added;
        }

        private void SetBusy(bool value)
        {
            _mcpBusy = value;
            Notify(nameof(CanChangeMcp));
            for (var i = 0; i < Servers.Count; i++)
            {
                Servers[i].Raise();
            }
        }

        private bool Contains(string url)
        {
            var servers = _store.Value.McpServers;
            for (var i = 0; i < servers.Count; i++)
            {
                if (string.Equals(servers[i].Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private McpServerRecord Find(string id)
        {
            var servers = _store.Value.McpServers;
            for (var i = 0; i < servers.Count; i++)
            {
                if (servers[i].Id == id)
                {
                    return servers[i];
                }
            }

            return null;
        }

        private McpServerItem FindRow(string id)
        {
            for (var i = 0; i < Servers.Count; i++)
            {
                if (Servers[i].Id == id)
                {
                    return Servers[i];
                }
            }

            return null;
        }

        private bool Save()
        {
            try
            {
                _store.Save();
                _saveFailed = false;
                Notify(nameof(SaveError));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _saveFailed = true;
                DiagnosticLog.Current.Write("preferences_save_failed", exception: ex);
                Notify(nameof(SaveError));
                return false;
            }
        }
    }
}
