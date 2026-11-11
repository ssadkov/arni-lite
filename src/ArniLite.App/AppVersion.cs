using System;
using System.Reflection;
using ArniLite.Core;
using ArniLite.Storage;
using ArniLite.Windows;

namespace ArniLite
{
    public static class AppVersion
    {
        public static string Full => typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppVersion).Assembly.GetName().Version.ToString();
        public static string Display => Full.Split('+')[0];
        public static string Platform => Environment.Is64BitProcess ? "x64" : "x86";
        public static string EnvironmentSummary => "Arni Lite " + Full + Environment.NewLine
            + "architecture=" + Platform + Environment.NewLine
            + "windows=" + new OsProbe().Detect().Description + Environment.NewLine
            + "framework=" + FrameworkProbe.Describe() + Environment.NewLine
            + "language=" + StringCatalog.Language + Environment.NewLine;
    }
}
