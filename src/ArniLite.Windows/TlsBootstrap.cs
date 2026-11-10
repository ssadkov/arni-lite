using System;
using System.Net;
using ArniLite.Core;

namespace ArniLite.Windows
{
    public static class TlsBootstrap
    {
        public static string Configure(OsFacts os)
        {
            if (os != null && !os.IsAtLeastWindows10)
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                return "TLS 1.2 is set for this process because the OS is older than Windows 10.";
            }

            return "TLS is left at the operating system default.";
        }
    }
}
