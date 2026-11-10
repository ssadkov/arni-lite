using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ArniLite.Core;
using ArniLite.Storage;

namespace ArniLite.Windows
{
    public sealed class DpapiTokenStore : ITokenStore
    {
        private readonly string _path;

        public DpapiTokenStore()
            : this(Path.Combine(AppPaths.DataDirectory, "session.bin"))
        {
        }

        public DpapiTokenStore(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public void Save(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentException("Token is empty.", nameof(token));
            }

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            var temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            File.Move(temporary, _path);
        }

        public string? Load()
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(_path);
            var raw = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }

        public void Clear()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
