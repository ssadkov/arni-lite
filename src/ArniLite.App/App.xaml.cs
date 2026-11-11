using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using ArniLite.Windows;
using ArniLite.Core;
using ArniLite.Storage;

namespace ArniLite
{
    public partial class App : Application
    {
        private static Mutex _instance;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            var preferences = new PreferencesStore();
            StringCatalog.SetLanguage(preferences.Value.Language);
            DiagnosticLog.Current.Write("app_start", phase: AppVersion.Display + "-" + AppVersion.Platform);
            var os = new OsProbe().Detect();
            TlsBootstrap.Configure(os);
            base.OnStartup(e);
            if (e.Args.Any(arg => arg == "--self-check"))
            {
                var output = e.Args.SkipWhile(arg => arg != "--self-check").Skip(1).FirstOrDefault();
                SelfCheck.Run(os, string.IsNullOrWhiteSpace(output) ? "g0-self-check.txt" : output);
                Shutdown(0);
                return;
            }

            _instance = new Mutex(true, @"Local\ArniLite.SingleInstance", out var created);
            if (!created)
            {
                MessageBox.Show(StringCatalog.Get("AlreadyRunning"));
                Shutdown(0);
                return;
            }

            var window = new MainWindow(os, preferences);
            window.Show();
        }

        private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            DiagnosticLog.Current.Write("unhandled_exception", exception: args.Exception);
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arnion", "ArniLite");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "crash.log"), DateTimeOffset.Now.ToString("o") + Environment.NewLine + args.Exception + Environment.NewLine);
            }
            catch (Exception)
            {
            }
        }
    }
}
