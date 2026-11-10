using System;
using System.Runtime.InteropServices;
using ArniLite.Core;

namespace ArniLite.Windows
{
    public sealed class OsProbe : IOsProbe
    {
        public OsFacts Detect()
        {
            var info = new RtlOsVersionInfo { dwOSVersionInfoSize = Marshal.SizeOf(typeof(RtlOsVersionInfo)) };
            if (RtlGetVersion(ref info) == 0)
            {
                return new OsFacts
                {
                    Major = (int)info.dwMajorVersion,
                    Minor = (int)info.dwMinorVersion,
                    Build = (int)info.dwBuildNumber,
                    ServicePack = info.szCSDVersion ?? string.Empty,
                    Source = "RtlGetVersion",
                };
            }

            var fallback = Environment.OSVersion.Version;
            return new OsFacts
            {
                Major = fallback.Major,
                Minor = fallback.Minor,
                Build = fallback.Build,
                Source = "Environment.OSVersion",
            };
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref RtlOsVersionInfo versionInfo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RtlOsVersionInfo
        {
            public int dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }
    }
}
