using System;
using ArniLite.Storage;

namespace ArniLite.Windows
{
    public sealed class McpSecretCatalog
    {
        private readonly DpapiTokenStore _store;
        private string _document = "{}";
        private string _legacy = string.Empty;

        public McpSecretCatalog(DpapiTokenStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            try
            {
                var raw = store.Load();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                if (McpSecretMap.IsDocument(raw))
                {
                    _document = raw.Trim();
                    return;
                }

                _legacy = raw.Trim();
                if (_legacy.Length > 500)
                {
                    _legacy = _legacy.Substring(0, 500);
                }
            }
            catch (Exception)
            {
                _document = "{}";
                _legacy = string.Empty;
            }
        }

        public bool HasLegacy => _legacy.Length > 0;

        public void AdoptLegacy(string id)
        {
            if (_legacy.Length == 0 || string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            _document = McpSecretMap.Upsert(_document, id, _legacy);
            _legacy = string.Empty;
            Persist();
        }

        public string Load(string id)
        {
            try
            {
                return McpSecretMap.Read(_document, id);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public void Save(string id, string token)
        {
            token = (token ?? string.Empty).Trim();
            if (token.Length > 500)
            {
                token = token.Substring(0, 500);
            }

            _document = token.Length == 0 ? McpSecretMap.Remove(_document, id) : McpSecretMap.Upsert(_document, id, token);
            Persist();
        }

        public void Remove(string id)
        {
            _document = McpSecretMap.Remove(_document, id);
            Persist();
        }

        private void Persist()
        {
            if (McpSecretMap.IsEmpty(_document))
            {
                _store.Clear();
                return;
            }

            _store.Save(_document);
        }
    }
}
