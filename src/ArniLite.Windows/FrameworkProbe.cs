using System;
using Microsoft.Win32;

namespace ArniLite.Windows
{
    public static class FrameworkProbe
    {
        public static string Describe()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            var release = key?.GetValue("Release");
            if (!(release is int value))
            {
                return "Release key not found";
            }

            var name = value >= 533320 ? "4.8.1 or later"
                : value >= 528040 ? "4.8"
                : value >= 461808 ? "4.7.2"
                : "older than 4.8";
            return name + " (release " + value + ")";
        }
    }
}
