using System.Diagnostics;
using ArniLite.Core;

namespace ArniLite.Windows
{
    public static class ExternalBrowser
    {
        public static void Open(string uri)
        {
            AuthLink.EnsureSafe(uri);
            using (Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            }))
            {
            }
        }
    }
}
