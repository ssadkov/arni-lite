using System.Linq;
using System.Windows;
using ArniLite.Windows;

namespace ArniLite
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
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

            var window = new MainWindow(os);
            window.Show();
        }
    }
}
