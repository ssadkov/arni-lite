using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Windows;

namespace ArniLite
{
    public static class SelfCheck
    {
        public static void Run(OsFacts os, string path)
        {
            var report = Task.Run(() => RunAsync(os)).GetAwaiter().GetResult();
            File.WriteAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static async Task<string> RunAsync(OsFacts os)
        {
            var report = new StringBuilder();
            report.AppendLine("Arni Lite G0 self-check");
            report.AppendLine("os=" + os.Description + " source=" + os.Source + " windows10plus=" + os.IsAtLeastWindows10);
            report.AppendLine("framework=" + FrameworkProbe.Describe());
            report.AppendLine("tls-policy=" + (os.IsAtLeastWindows10 ? "system-default" : "tls12"));

            var http = new System.Net.Http.HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            var api = new ArniApiClient(http);
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var tls = await api.ProbeTlsAsync(probeTimeout.Token).ConfigureAwait(false);
            report.AppendLine("tls=" + tls.Status + " " + tls.Detail);

            var tokenPath = Path.Combine(Path.GetTempPath(), "arni-lite-dpapi-" + Guid.NewGuid().ToString("N") + ".bin");
            var store = new DpapiTokenStore(tokenPath);
            store.Save("arni-lite-self-check");
            var loaded = store.Load();
            store.Clear();
            report.AppendLine("dpapi=" + (loaded == "arni-lite-self-check" && !File.Exists(tokenPath) ? "pass" : "fail"));

            var runner = new ProcessRunner();
            using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var command = await runner.RunPowerShellAsync("Write-Output 'arni-lite-ok'\nWrite-Output 'проверка'\n", 15000, commandTimeout.Token).ConfigureAwait(false);
            report.AppendLine("powershell.status=" + command.Status);
            report.AppendLine("powershell.exit=" + (command.ExitCode.HasValue ? command.ExitCode.Value.ToString() : "none"));
            report.AppendLine("powershell.job=" + command.JobAssigned + " " + command.JobDetail);
            report.AppendLine("powershell.ok=" + command.Stdout.Contains("arni-lite-ok"));
            report.AppendLine("powershell.cyrillic=" + command.Stdout.Contains("проверка"));
            if (!string.IsNullOrWhiteSpace(command.Stderr))
            {
                report.AppendLine("powershell.stderr=" + command.Stderr.Trim());
            }

            using var kill = new CancellationTokenSource();
            var running = runner.RunPowerShellAsync("ping.exe -n 20 127.0.0.1\n", 20000, kill.Token);
            await Task.Delay(1000).ConfigureAwait(false);
            kill.Cancel();
            var stopped = await running.ConfigureAwait(false);
            report.AppendLine("stop.status=" + stopped.Status);
            report.AppendLine("stop.job=" + stopped.JobAssigned);
            report.AppendLine("stop.survivors=" + (stopped.Survivors.Count == 0 ? "none" : string.Join(", ", stopped.Survivors)));

            using (var current = Process.GetCurrentProcess())
            {
                report.AppendLine("private-bytes=" + current.PrivateMemorySize64);
            }

            report.AppendLine("win7=not-run");
            report.AppendLine("server2012=not-run");
            return report.ToString();
        }
    }
}
