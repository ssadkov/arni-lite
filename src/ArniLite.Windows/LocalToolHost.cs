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
        private readonly ArniLite.Api.ArniApiClient _api;
        private readonly Func<string> _token;
        private ArniLite.Api.McpBundle _remote;

        public LocalToolHost(WorkspaceService workspace, IProcessRunner processes, ISideEffectJournal journal, IUserApproval approval, ArniLite.Api.ArniApiClient api, Func<string> token)
        {
            _workspace = workspace;
            _processes = processes;
            _journal = journal;
            _approval = approval;
            _api = api;
            _token = token ?? (() => string.Empty);
        }

        public void UseRemote(ArniLite.Api.McpBundle session)
        {
            _remote = session;
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
                    case "read_skill":
                        return ReadSkill(args);
                    case "web_search":
                        return await WebSearchAsync(args, cancellationToken).ConfigureAwait(false);
                    case "propose_file_change":
                        return await ChangeAsync(turnId, round, call, args, cancellationToken).ConfigureAwait(false);
                    case "generate_image":
                        return await ImageAsync(turnId, round, call, args, cancellationToken).ConfigureAwait(false);
                    case "run_command":
                        return await CommandAsync(turnId, round, call, args, cancellationToken).ConfigureAwait(false);
                    default:
                        if (_remote != null && _remote.Owns(call.Name))
                        {
                            return await RemoteAsync(turnId, round, call, cancellationToken).ConfigureAwait(false);
                        }

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

        private async Task<ToolExecution> WebSearchAsync(JObject args, CancellationToken cancellationToken)
        {
            var query = ((string)args["query"] ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                return Fail("failed", "Provide a search query.", false);
            }

            if (query.Length > 300)
            {
                query = query.Substring(0, 300);
            }

            var token = _token();
            if (string.IsNullOrEmpty(token))
            {
                return Fail("failed", "Sign in to Arni to search the web.", false);
            }

            try
            {
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(20));
                    var results = await _api.SearchWebAsync(token, query, limit.Token).ConfigureAwait(false);
                    var json = new JObject
                    {
                        ["schema_version"] = 1,
                        ["status"] = "completed",
                        ["result"] = new JObject { ["results"] = results },
                    }.ToString(Newtonsoft.Json.Formatting.None);
                    return Done(json);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail("failed", "Web search timed out.", false);
            }
        }

        private ToolExecution ReadSkill(JObject args)
        {
            var name = ((string)args["name"] ?? string.Empty).Trim();
            var text = SkillCatalog.Read(_workspace.Root, name);
            if (text.Length == 0)
            {
                return Fail("failed", "Unknown skill.", false);
            }

            return Done(new JObject
            {
                ["schema_version"] = 1,
                ["status"] = "completed",
                ["result"] = new JObject { ["name"] = name, ["text"] = text },
            }.ToString(Newtonsoft.Json.Formatting.None));
        }

        private async Task<ToolExecution> RemoteAsync(string turnId, int round, ToolCallRecord call, CancellationToken cancellationToken)
        {
            var key = HistoryKey.ToolKey(turnId, round, call.Id);
            var hash = HistoryKey.ArgumentsHash(call.ArgumentsJson);
            if (!SideEffectGuard.TryPrepare(_journal, key, hash, out var blockedStatus, out var blockedJson))
            {
                return new ToolExecution { Status = blockedStatus, ContentJson = blockedJson, SideEffect = true };
            }

            var preview = call.ArgumentsJson ?? string.Empty;
            if (preview.Length > 400)
            {
                preview = preview.Substring(0, 400);
            }

            var allowed = _approval.AllowSideEffectsForSession
                || await _approval.ApproveAsync("Вызвать " + call.Name, preview, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                var denied = SideEffectGuard.Result("denied", "The user denied the MCP tool.");
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "denied", ResultJson = denied });
                return new ToolExecution { Status = "denied", ContentJson = denied, SideEffect = true };
            }

            cancellationToken.ThrowIfCancellationRequested();
            _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "executing" });
            try
            {
                var called = await _remote.CallAsync(call.Name, call.ArgumentsJson, cancellationToken).ConfigureAwait(false);
                var status = called.IsError ? "failed" : "completed";
                var json = new JObject
                {
                    ["schema_version"] = 1,
                    ["tool_call_id"] = call.Id,
                    ["status"] = status,
                    ["result"] = new JObject { ["text"] = called.Text },
                }.ToString(Newtonsoft.Json.Formatting.None);
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = status, ResultJson = json });
                return new ToolExecution { Status = status, ContentJson = json, SideEffect = true };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failed = SideEffectGuard.Result("failed", ex.Message);
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "failed", ResultJson = failed });
                return new ToolExecution { Status = "failed", ContentJson = failed, SideEffect = true };
            }
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

        private async Task<ToolExecution> ImageAsync(string turnId, int round, ToolCallRecord call, JObject args, CancellationToken cancellationToken)
        {
            var prompt = ((string)args["prompt"] ?? string.Empty).Trim();
            var savePath = (string)args["save_path"] ?? string.Empty;
            var ratio = ImageAsset.NormalizeRatio((string)args["aspect_ratio"]);
            if (prompt.Length == 0 || prompt.Length > ImageAsset.MaxPromptLength)
            {
                return Fail("failed", "Provide an image description of up to 10,000 characters.", true);
            }

            if (ImageAsset.HasUnsupportedScript(prompt))
            {
                return Fail("failed", "Write the image description in English. Cyrillic text is rejected by the image model.", true);
            }

            if (ratio == null)
            {
                return Fail("failed", "Choose one of the supported aspect ratios.", true);
            }

            if (string.IsNullOrEmpty(_workspace.Root))
            {
                return Fail("failed", "Open one local project folder to save generated images.", true);
            }

            string full;
            try
            {
                full = _workspace.NewPngPath(savePath);
            }
            catch (Exception ex)
            {
                return Fail("failed", ex.Message, true);
            }

            var token = _token();
            if (string.IsNullOrEmpty(token))
            {
                return Fail("failed", "Sign in to Arni to generate images.", true);
            }

            var key = HistoryKey.ToolKey(turnId, round, call.Id);
            var hash = HistoryKey.ArgumentsHash(call.ArgumentsJson);
            if (!SideEffectGuard.TryPrepare(_journal, key, hash, out var blockedStatus, out var blockedJson))
            {
                return new ToolExecution { Status = blockedStatus, ContentJson = blockedJson, SideEffect = true };
            }

            var allowed = _approval.AllowSideEffectsForSession
                || await _approval.ApproveAsync("Сгенерировать картинку", "Stable Image Core, $0.04\n" + savePath + "\n" + prompt, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                var denied = SideEffectGuard.Result("denied", "The user denied image generation.");
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "denied", ResultJson = denied });
                return new ToolExecution { Status = "denied", ContentJson = denied, SideEffect = true };
            }

            cancellationToken.ThrowIfCancellationRequested();
            _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "executing" });
            try
            {
                ArniLite.Api.GeneratedImage image;
                using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(240)))
                {
                    image = await _api.GenerateImageAsync(token, prompt, ratio, ImageAsset.RequestKey(_workspace.Root, savePath, prompt, ratio), limit.Token).ConfigureAwait(false);
                }
                full = _workspace.NewPngPath(savePath);
                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(image.Png, 0, image.Png.Length);
                }

                var json = new JObject
                {
                    ["schema_version"] = 1,
                    ["status"] = "completed",
                    ["result"] = new JObject
                    {
                        ["path"] = full,
                        ["model"] = image.Model,
                        ["cost_micros"] = image.CostMicros,
                    },
                }.ToString(Newtonsoft.Json.Formatting.None);
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "completed", ResultJson = json });
                return new ToolExecution { Status = "completed", ContentJson = json, SideEffect = true };
            }
            catch (OperationCanceledException)
            {
                var timedOut = SideEffectGuard.Result("failed", "Image generation timed out. Retry with the same description and path to recover it without a second charge.");
                _journal.Append(new SideEffectEntry { Key = key, ArgumentsHash = hash, State = "failed", ResultJson = timedOut });
                return new ToolExecution { Status = "failed", ContentJson = timedOut, SideEffect = true };
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
                File.WriteAllText(script, CmdScript.Text(command), CmdScript.Encoding);
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
