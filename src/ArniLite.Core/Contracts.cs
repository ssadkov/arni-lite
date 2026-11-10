using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArniLite.Core
{
    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    public sealed class OsFacts
    {
        public int Major { get; set; }
        public int Minor { get; set; }
        public int Build { get; set; }
        public string ServicePack { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;

        public bool IsAtLeastWindows10 => Major >= 10;

        public string Description => Major + "." + Minor + "." + Build
            + (string.IsNullOrEmpty(ServicePack) ? string.Empty : " " + ServicePack);
    }

    public interface IOsProbe
    {
        OsFacts Detect();
    }

    public sealed class DiagnosticItem
    {
        public DiagnosticItem(string name, string status, string detail)
        {
            Name = name;
            Status = status;
            Detail = detail;
        }

        public string Name { get; }
        public string Status { get; }
        public string Detail { get; }
    }

    public interface ITokenStore
    {
        void Save(string token);
        string? Load();
        void Clear();
    }

    public sealed class ProcessRunResult
    {
        public string Status { get; set; } = "failed";
        public int? ExitCode { get; set; }
        public string Stdout { get; set; } = string.Empty;
        public string Stderr { get; set; } = string.Empty;
        public bool Truncated { get; set; }
        public bool JobAssigned { get; set; }
        public string JobDetail { get; set; } = string.Empty;
        public List<string> Survivors { get; set; } = new List<string>();
        public long DurationMs { get; set; }
    }

    public interface IProcessRunner
    {
        Task<ProcessRunResult> RunPowerShellAsync(string command, int timeoutMs, CancellationToken cancellationToken);
    }
}
