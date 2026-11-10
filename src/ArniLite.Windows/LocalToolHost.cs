using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Core;
using Newtonsoft.Json.Linq;

namespace ArniLite.Windows
{
    public sealed class LocalToolHost : IToolExecutor
    {
        private readonly WorkspaceService _workspace;
        private readonly IProcessRunner _processes;
        private readonly ISideEffectJournal _journal;
        private readonly IUserApproval _approval;

        public LocalToolHost(WorkspaceService workspace, IProcessRunner processes, ISideEffectJournal journal, IUserApproval approval)
        {
            _workspace = workspace;
            _processes = processes;
            _journal = journal;
            _approval = approval;
        }

        public async Task<ToolExecution> ExecuteAsync(string turnId, int round, ToolCallRecord call, CancellationToken cancellationToken)
        {
            try
            {
                var args = JObject.Parse(call.ArgumentsJson);
                switch (call.Name)
                {
                    case "list_files":
                        return Done(List(args));
                    case "read_file":
                        return Done(Read(args));
                    case "search_text":
                        return Done(Search(args));
                    case "propose_file_change":
                        return await ChangeAsync(turnId, round, call, args, cancellationToken).ConfigureAwait(false);
                    case "run_command":
                        return await CommandAsync(turnId, round, call, args, cancellationToken).ConfigureAwait(false);
                    default:
                        return Fail("failed", "Unknown tool.", false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return Fail("failed", ex.Message, false);
            }
        }

        private string List(JObject args)
        {
            var entries = _workspace.List((string)args["path"] ?? string.Empty, (int?)args["depth"] ?? 1, (int?)args["limit"] ?? 50);
            var array = new JArray();
            for (var i = 0; i < entries.Count; i++)
            {
                array.Add(entries[i]);
            }
            return new JObject
            {
                ["schema_version"] = 1,
                ["status"] = "completed",
                ["result"] = new JObject { ["entries"] = array },
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private string Read(JObject args)
        {
            var slice = _workspace.Read((string)args["path"] ?? string.Empty, (int?)args["start_line"] ?? 1, (int?)args["max_lines"] ?? WorkspaceService.MaxReadLines);
            return new JObject
            {
                ["schema_version"] = 1,
                ["status"] = "completed",
                ["result"] = new JObject
                {
                    ["path"] = slice.Path,
                    ["text"] = slice.Text,
                    ["encoding"] = slice.Encoding,
                    ["newline"] = slice.Newline,
                    ["hash"] = slice.Hash,
                    ["start_line"] = slice.StartLine,
                    ["line_count"] = slice.LineCount,
                    ["truncated"] = slice.Truncated,
                },
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private string Search(JObject args)
        {
            var matches = _workspace.Search((string)args["query"] ?? string.Empty, (string)args["path"] ?? string.Empty, (int?)args["limit"] ?? 20);
            var array = new JArray();
            for (var i = 0; i < matches.Count; i++)
            {
                array.Add(matches[i]);
            }
            return new JObject
            {
                ["schema_version"] = 1,
                ["status"] = "completed",
                ["result"] = new JObject { ["matches"] = array },
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private async Task<ToolExecution> ChangeAsync(string turnId, int round, ToolCallRecord call, JObject args, CancellationToken cancellationToken)
        {
            var proposal = _workspace.Propose(
                (string)args["path"] ?? string.Empty,
                (string)args["expected_hash"] ?? string.Empty,
                (bool?)args["must_not_exist"] ?? false,
                (string)args["content"] ?? string.Empty);
            var key = HistoryKey.ToolKey(turnId, round, call.Id);
            var hash = HistoryKey.ArgumentsHash(call.ArgumentsJson);
            if (!SideEffectGuard.TryPrepare(_journal, key, hash, out var blockedStatus, out var blockedJson))
            {
                return new ToolExecution { Status = blockedStatus, ContentJson = blockedJson, SideEffect = true };
            }

            var allowed = _approval.AllowSideEffectsForSession
                || await _approval.ApproveAsync("Записать " + proposal.RelativePath, proposal.Preview, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                var denied = SideEffectGuard.Result("denied", "The user denied the file change.");
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "denied", ResultJson = denied });
                return new ToolExecution { Status = "denied", ContentJson = denied, SideEffect = true };
            }

            _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "executing" });
            try
            {
                var newHash = _workspace.Apply(proposal);
                var json = new JObject
                {
                    ["schema_version"] = 1,
                    ["status"] = "completed",
                    ["result"] = new JObject { ["path"] = proposal.RelativePath, ["hash"] = newHash },
                }.ToString(Newtonsoft.Json.Formatting.None);
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "completed", ResultJson = json });
                return new ToolExecution { Status = "completed", ContentJson = json, SideEffect = true };
            }
            catch (Exception ex)
            {
                var failed = SideEffectGuard.Result("failed", ex.Message);
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "failed", ResultJson = failed });
                return new ToolExecution { Status = "failed", ContentJson = failed, SideEffect = true };
            }
        }

        private async Task<ToolExecution> CommandAsync(string turnId, int round, ToolCallRecord call, JObject args, CancellationToken cancellationToken)
        {
            var shell = ((string)args["shell"] ?? string.Empty).ToLowerInvariant();
            var command = (string)args["command"] ?? string.Empty;
            if (command.Length == 0 || (shell != "powershell" && shell != "cmd"))
            {
                return Fail("failed", "shell must be powershell or cmd, and command must not be empty.", true);
            }

            string cwd;
            if (string.IsNullOrEmpty(_workspace.Root))
            {
                cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            else if (!WorkspacePath.TryCombine(_workspace.Root, (string)args["cwd"] ?? string.Empty, out cwd, out var error))
            {
                return Fail("failed", error, true);
            }

            var timeout = (int?)args["timeout_ms"] ?? 300000;
            if (timeout < 1000)
            {
                timeout = 1000;
            }

            if (timeout > 600000)
            {
                timeout = 600000;
            }

            var key = HistoryKey.ToolKey(turnId, round, call.Id);
            var hash = HistoryKey.ArgumentsHash(call.ArgumentsJson);
            if (!SideEffectGuard.TryPrepare(_journal, key, hash, out var blockedStatus, out var blockedJson))
            {
                return new ToolExecution { Status = blockedStatus, ContentJson = blockedJson, SideEffect = true };
            }

            var body = shell + " in " + cwd + Environment.NewLine + command
                + Environment.NewLine + "The command runs as the current user, not inside a sandbox.";
            var allowed = _approval.AllowSideEffectsForSession
                || await _approval.ApproveAsync("Запустить команду", body, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                var denied = SideEffectGuard.Result("denied", "The user denied the command.");
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "denied", ResultJson = denied });
                return new ToolExecution { Status = "denied", ContentJson = denied, SideEffect = true };
            }

            _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "executing" });
            ProcessRunResult ran;
            if (shell == "powershell")
            {
                ran = await _processes.RunProcessAsync(
                    Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoLogo -NoProfile -NonInteractive -Command -",
                    command,
                    cwd,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var script = Path.Combine(Path.GetTempPath(), "arni-lite-" + Guid.NewGuid().ToString("N") + ".cmd");
                File.WriteAllText(script, "@echo off\r\n" + command + "\r\n", new UTF8Encoding(true));
                try
                {
                    ran = await _processes.RunProcessAsync(
                        Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                        "/D /C \"" + script + "\"",
                        null,
                        cwd,
                        timeout,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (File.Exists(script))
                    {
                        File.Delete(script);
                    }
                }
            }

            var status = ran.Status == "completed" ? "completed" : ran.Status;
            var json = new JObject
            {
                ["schema_version"] = 1,
                ["tool_call_id"] = call.Id,
                ["status"] = status,
                ["result"] = new JObject
                {
                    ["exit_code"] = ran.ExitCode,
                    ["stdout"] = ran.Stdout,
                    ["stderr"] = ran.Stderr,
                    ["duration_ms"] = ran.DurationMs,
                    ["truncated"] = ran.Truncated,
                    ["job_assigned"] = ran.JobAssigned,
                    ["survivors"] = new JArray(ran.Survivors),
                },
            }.ToString(Newtonsoft.Json.Formatting.None);
            _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = status, ResultJson = json });
            return new ToolExecution { Status = status, ContentJson = json, SideEffect = true };
        }

        private static ToolExecution Done(string json)
        {
            return new ToolExecution { Status = "completed", ContentJson = json, SideEffect = false };
        }

        private static ToolExecution Fail(string status, string message, bool sideEffect)
        {
            return new ToolExecution
            {
                Status = status,
                SideEffect = sideEffect,
                ContentJson = SideEffectGuard.Result(status, message),
            };
        }
    }
}
