using System;
using System.IO;

namespace ArniLite.Storage
{
    public static class AppPaths
    {
        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arnion", "ArniLite");
    }
}
