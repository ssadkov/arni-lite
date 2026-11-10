using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Core;
using Microsoft.Win32.SafeHandles;

namespace ArniLite.Windows
{
    public sealed class ProcessRunner : IProcessRunner
    {
        public const int OutputCharLimit = 64 * 1024;

        public Task<ProcessRunResult> RunPowerShellAsync(string command, int timeoutMs, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new ArgumentException("Command is empty.", nameof(command));
            }

            if (timeoutMs < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            }

            return Task.Run(() => Run(command, timeoutMs, cancellationToken), cancellationToken);
        }

        private static ProcessRunResult Run(string command, int timeoutMs, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            var result = new ProcessRunResult();
            var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershell))
            {
                result.Status = "failed";
                result.JobDetail = "Windows PowerShell was not found.";
                result.DurationMs = started.ElapsedMilliseconds;
                return result;
            }

            IntPtr job = IntPtr.Zero;
            IntPtr processHandle = IntPtr.Zero;
            IntPtr threadHandle = IntPtr.Zero;
            IntPtr stdinRead = IntPtr.Zero;
            IntPtr stdinWrite = IntPtr.Zero;
            IntPtr stdoutRead = IntPtr.Zero;
            IntPtr stdoutWrite = IntPtr.Zero;
            IntPtr stderrRead = IntPtr.Zero;
            IntPtr stderrWrite = IntPtr.Zero;
            var jobAssigned = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                job = CreateJob();
                CreatePipePair(out stdinRead, out stdinWrite, parentReads: false);
                CreatePipePair(out stdoutRead, out stdoutWrite, parentReads: true);
                CreatePipePair(out stderrRead, out stderrWrite, parentReads: true);

                var startup = new StartupInfo
                {
                    cb = Marshal.SizeOf(typeof(StartupInfo)),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = stdinRead,
                    hStdOutput = stdoutWrite,
                    hStdError = stderrWrite,
                };
                var commandLine = new StringBuilder(Quote(powershell) + " -NoLogo -NoProfile -NonInteractive -Command -");
                if (!CreateProcess(powershell, commandLine, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended | CreateNoWindow, IntPtr.Zero, Path.GetTempPath(), ref startup, out var info))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                processHandle = info.hProcess;
                threadHandle = info.hThread;
                Close(ref stdinRead);
                Close(ref stdoutWrite);
                Close(ref stderrWrite);

                if (AssignProcessToJobObject(job, processHandle))
                {
                    jobAssigned = true;
                    result.JobAssigned = true;
                    result.JobDetail = "Process is in a job object. Stop terminates the job.";
                }
                else
                {
                    result.JobDetail = "Job assignment failed (Win32 " + Marshal.GetLastWin32Error() + "). Stop walks the process tree and may leave children.";
                }

                if (ResumeThread(threadHandle) == 0xFFFFFFFF)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var stdout = new OutputBuffer();
                var stderr = new OutputBuffer();
                var stdoutOwned = new SafeFileHandle(stdoutRead, ownsHandle: true);
                var stderrOwned = new SafeFileHandle(stderrRead, ownsHandle: true);
                stdoutRead = IntPtr.Zero;
                stderrRead = IntPtr.Zero;
                var stdoutTask = Task.Run(() => Drain(stdoutOwned, stdout));
                var stderrTask = Task.Run(() => Drain(stderrOwned, stderr));
                using (var stdin = new FileStream(new SafeFileHandle(stdinWrite, ownsHandle: true), FileAccess.Write))
                {
                    stdinWrite = IntPtr.Zero;
                    var payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(command);
                    if (payload.Length == 0 || payload[payload.Length - 1] != (byte)'\n')
                    {
                        var withNewLine = new byte[payload.Length + 1];
                        Buffer.BlockCopy(payload, 0, withNewLine, 0, payload.Length);
                        withNewLine[payload.Length] = (byte)'\n';
                        payload = withNewLine;
                    }

                    stdin.Write(payload, 0, payload.Length);
                }

                var status = Wait(processHandle, timeoutMs, cancellationToken, () => StopTree(job, jobAssigned, processHandle, info.dwProcessId));
                Task.WaitAll(new[] { stdoutTask, stderrTask }, 2000);
                result.Status = status;
                result.Stdout = stdout.Text.ToString();
                result.Stderr = stderr.Text.ToString();
                result.Truncated = stdout.Truncated || stderr.Truncated;
                if (GetExitCodeProcess(processHandle, out var exitCode))
                {
                    result.ExitCode = exitCode;
                }

                if (status != "completed")
                {
                    Thread.Sleep(200);
                    result.Survivors = FindSurvivors((uint)info.dwProcessId);
                }
            }
            catch (OperationCanceledException)
            {
                result.Status = "cancelled";
            }
            catch (Exception ex)
            {
                result.Status = "failed";
                result.Stderr = ex.Message;
            }
            finally
            {
                Close(ref stdinRead);
                Close(ref stdinWrite);
                Close(ref stdoutRead);
                Close(ref stdoutWrite);
                Close(ref stderrRead);
                Close(ref stderrWrite);
                Close(ref threadHandle);
                Close(ref processHandle);
                Close(ref job);
                result.DurationMs = started.ElapsedMilliseconds;
            }

            return result;
        }

        private static string Wait(IntPtr processHandle, int timeoutMs, CancellationToken cancellationToken, Action stop)
        {
            var wait = Stopwatch.StartNew();
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    stop();
                    return "cancelled";
                }

                if (wait.ElapsedMilliseconds >= timeoutMs)
                {
                    stop();
                    return "timed_out";
                }

                var slice = (uint)Math.Min(200, Math.Max(1, timeoutMs - (int)wait.ElapsedMilliseconds));
                var code = WaitForSingleObject(processHandle, slice);
                if (code == WaitObject0)
                {
                    return "completed";
                }
            }
        }

        private static void StopTree(IntPtr job, bool jobAssigned, IntPtr processHandle, int rootPid)
        {
            if (jobAssigned && job != IntPtr.Zero)
            {
                TerminateJobObject(job, 1);
                return;
            }

            foreach (var pid in Descendants((uint)rootPid))
            {
                var handle = OpenProcess(ProcessTerminate, false, pid);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                TerminateProcess(handle, 1);
                CloseHandle(handle);
            }

            if (processHandle != IntPtr.Zero)
            {
                TerminateProcess(processHandle, 1);
            }
        }

        private static IntPtr CreateJob()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var info = new JobObjectExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            var length = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info, (uint)length))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new Win32Exception(error);
            }

            return job;
        }

        private static void Drain(SafeFileHandle handle, OutputBuffer kept)
        {
            using var stream = new FileStream(handle, FileAccess.Read);
            var buffer = new byte[8192];
            Decoder decoder = Encoding.UTF8.GetDecoder();
            var primed = false;
            try
            {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (!primed)
                {
                    primed = true;
                    if ((read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) || (read >= 4 && buffer[1] == 0 && buffer[3] == 0))
                    {
                        decoder = Encoding.Unicode.GetDecoder();
                    }
                }

                var chars = new char[read];
                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                var start = 0;
                if (kept.Text.Length == 0 && count > 0 && chars[0] == '\uFEFF')
                {
                    start = 1;
                }

                var available = count - start;
                var room = OutputCharLimit - kept.Text.Length;
                if (room <= 0)
                {
                    kept.Truncated = true;
                    continue;
                }

                if (available > room)
                {
                    kept.Text.Append(chars, start, room);
                    kept.Truncated = true;
                }
                else
                {
                    kept.Text.Append(chars, start, available);
                }
            }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static List<string> FindSurvivors(uint rootPid)
        {
            var names = Snapshot();
            var alive = new HashSet<uint>();
            foreach (var entry in names)
            {
                alive.Add(entry.Pid);
            }

            var survivors = new List<string>();
            foreach (var pid in Descendants(rootPid))
            {
                if (!alive.Contains(pid))
                {
                    continue;
                }

                survivors.Add(names.Find(entry => entry.Pid == pid).Name + " (" + pid + ")");
            }

            return survivors;
        }

        private static List<uint> Descendants(uint rootPid)
        {
            var entries = Snapshot();
            var children = new Dictionary<uint, List<uint>>();
            foreach (var entry in entries)
            {
                if (!children.TryGetValue(entry.Parent, out var list))
                {
                    list = new List<uint>();
                    children[entry.Parent] = list;
                }

                list.Add(entry.Pid);
            }

            var ordered = new List<uint>();
            var seen = new HashSet<uint>();
            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                var pid = stack.Pop();
                if (!seen.Add(pid))
                {
                    continue;
                }

                ordered.Add(pid);
                if (!children.TryGetValue(pid, out var kids))
                {
                    continue;
                }

                foreach (var kid in kids)
                {
                    stack.Push(kid);
                }
            }

            ordered.Reverse();
            return ordered;
        }

        private static List<ProcessEntry> Snapshot()
        {
            var list = new List<ProcessEntry>();
            var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return list;
            }

            try
            {
                var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
                if (!Process32FirstW(snapshot, ref entry))
                {
                    return list;
                }

                do
                {
                    list.Add(new ProcessEntry(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile ?? string.Empty));
                    entry.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                }
                while (Process32NextW(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return list;
        }

        private static void CreatePipePair(out IntPtr read, out IntPtr write, bool parentReads)
        {
            var attributes = new SecurityAttributes
            {
                nLength = Marshal.SizeOf(typeof(SecurityAttributes)),
                bInheritHandle = true,
            };
            if (!CreatePipe(out read, out write, ref attributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var parentEnd = parentReads ? read : write;
            if (!SetHandleInformation(parentEnd, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void Close(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        private sealed class OutputBuffer
        {
            public StringBuilder Text { get; } = new StringBuilder();
            public bool Truncated { get; set; }
        }

        private struct ProcessEntry
        {
            public ProcessEntry(uint pid, uint parent, string name)
            {
                Pid = pid;
                Parent = parent;
                Name = name;
            }

            public uint Pid { get; }
            public uint Parent { get; }
            public string Name { get; }
        }

        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const int StartfUseStdHandles = 0x00000100;
        private const uint HandleFlagInherit = 1;
        private const uint WaitObject0 = 0;
        private const uint Th32CsSnapProcess = 0x00000002;
        private const uint ProcessTerminate = 0x0001;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    }
}
