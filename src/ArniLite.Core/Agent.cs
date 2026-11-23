using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ArniLite.Core
{
    public sealed class ChatImagePart
    {
        public string MediaType { get; set; } = "image/png";
        public string DataBase64 { get; set; } = string.Empty;
    }

    public sealed class ChatMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string ToolCallId { get; set; } = string.Empty;
        public List<ToolCallRecord> ToolCalls { get; set; } = new List<ToolCallRecord>();
        public List<ChatImagePart> Images { get; set; }
    }

    public static class AttachmentText
    {
        public const int MaxFiles = 20;
        public const int MaxChars = 12000;

        public static bool IsBinary(byte[] sample)
        {
            var length = sample == null ? 0 : Math.Min(sample.Length, 8000);
            for (var i = 0; i < length; i++)
            {
                if (sample[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static string Clip(string text, out bool truncated)
        {
            text = text ?? string.Empty;
            if (text.Length <= MaxChars)
            {
                truncated = false;
                return text;
            }

            truncated = true;
            return text.Substring(0, MaxChars);
        }

        public static string Compose(string prompt, string name, string body, bool truncated)
        {
            var block = (name ?? string.Empty) + ":\n" + (body ?? string.Empty);
            if (truncated)
            {
                block += "\n…";
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return block;
            }

            return prompt.Trim() + "\n\n" + block;
        }
    }

    public sealed class ToolCallRecord
    {
        public int Index { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = string.Empty;
    }

    public sealed class ModelTurn
    {
        public string Content { get; set; } = string.Empty;
        public string FinishReason { get; set; } = string.Empty;
        public bool Done { get; set; }
        public bool Interrupted { get; set; }
        public string Error { get; set; } = string.Empty;
        public List<ToolCallRecord> ToolCalls { get; set; } = new List<ToolCallRecord>();

        public bool HasToolCalls => ToolCalls != null && ToolCalls.Count > 0;
    }

    public interface IModelCompletion
    {
        Task<ModelTurn> CompleteAsync(IList<ChatMessage> messages, bool withTools, Action<string> onDelta, CancellationToken cancellationToken);
    }

    public sealed class ToolExecution
    {
        public string Status { get; set; } = "failed";
        public string ContentJson { get; set; } = "{}";
        public bool SideEffect { get; set; }
    }

    public interface IToolExecutor
    {
        Task<ToolExecution> ExecuteAsync(string turnId, int round, ToolCallRecord call, CancellationToken cancellationToken);
    }

    public sealed class AgentLimits
    {
        public int MaxModelRounds { get; set; } = 12;
        public int MaxToolCalls { get; set; } = 30;
        public TimeSpan MaxTurn { get; set; } = TimeSpan.FromMinutes(15);
    }

    public sealed class AgentRunResult
    {
        public string Status { get; set; } = "completed";
        public string Detail { get; set; } = string.Empty;
        public int ModelRounds { get; set; }
        public int ToolCalls { get; set; }
    }

    public sealed class AgentOrchestrator
    {
        private readonly IClock _clock;
        private readonly AgentLimits _limits;

        public AgentOrchestrator(IClock clock, AgentLimits limits)
        {
            _clock = clock ?? new SystemClock();
            _limits = limits ?? new AgentLimits();
        }

        public async Task<AgentRunResult> RunAsync(
            IList<ChatMessage> history,
            bool withTools,
            IModelCompletion model,
            IToolExecutor tools,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            var result = new AgentRunResult();
            var started = _clock.UtcNow;
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            // Tool name plus its error text; a model that repeats a call the tool already rejected
            // with the same error is not learning from it (2026-11-23: the same TickTick validation error twice).
            var failures = new HashSet<string>(StringComparer.Ordinal);
            while (result.ModelRounds < _limits.MaxModelRounds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_clock.UtcNow - started > _limits.MaxTurn)
                {
                    result.Status = "failed";
                    result.Detail = "The turn reached the time limit.";
                    return result;
                }

                result.ModelRounds++;
                var turn = await model.CompleteAsync(history, withTools, onDelta ?? (_ => { }), cancellationToken).ConfigureAwait(false);
                history.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = turn.Content ?? string.Empty,
                    ToolCalls = turn.ToolCalls ?? new List<ToolCallRecord>(),
                });

                if (!string.IsNullOrEmpty(turn.Error))
                {
                    result.Status = "failed";
                    result.Detail = turn.Error;
                    return result;
                }

                if (turn.Interrupted || !turn.Done)
                {
                    result.Status = "interrupted";
                    result.Detail = StreamInterrupted;
                    return result;
                }

                if (string.Equals(turn.FinishReason, "length", StringComparison.Ordinal))
                {
                    result.Status = "failed";
                    result.Detail = "The model hit the output limit before a finished tool call. Nothing was executed.";
                    return result;
                }

                if (!withTools || !turn.HasToolCalls || !string.Equals(turn.FinishReason, "tool_calls", StringComparison.Ordinal))
                {
                    result.Status = "completed";
                    return result;
                }

                var ordered = new List<ToolCallRecord>(turn.ToolCalls);
                ordered.Sort((left, right) => left.Index.CompareTo(right.Index));
                var stopAfterDenial = false;
                foreach (var call in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.ToolCalls++;
                    if (result.ToolCalls > _limits.MaxToolCalls)
                    {
                        result.Status = "failed";
                        result.Detail = "The turn reached the tool-call limit.";
                        return result;
                    }

                    var fingerprint = (call.Name ?? string.Empty) + "\n" + (call.ArgumentsJson ?? string.Empty);
                    ToolExecution execution;
                    if (blocked.Contains(fingerprint))
                    {
                        execution = new ToolExecution
                        {
                            Status = "denied",
                            SideEffect = true,
                            ContentJson = "{\"schema_version\":1,\"status\":\"denied\",\"result\":{\"message\":\"The same action was already denied in this turn.\"}}",
                        };
                        stopAfterDenial = true;
                    }
                    else
                    {
                        execution = await tools.ExecuteAsync(HistoryKey.TurnId(history), result.ModelRounds, call, cancellationToken).ConfigureAwait(false);
                        if (execution.SideEffect && (execution.Status == "denied" || execution.Status == "outcome_unknown" || execution.Status == "cancelled"))
                        {
                            blocked.Add(fingerprint);
                        }
                    }

                    history.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = call.Id,
                        Content = execution.ContentJson ?? "{}",
                    });
                    if (stopAfterDenial)
                    {
                        result.Status = "completed";
                        result.Detail = "A repeated side effect was not run again.";
                        return result;
                    }

                    if (execution.Status == "failed" && !failures.Add((call.Name ?? string.Empty) + "\n" + FailureText(execution.ContentJson)))
                    {
                        result.Status = "failed";
                        result.Detail = RepeatedToolFailure + (call.Name ?? string.Empty);
                        return result;
                    }
                }
            }

            result.Status = "failed";
            result.Detail = "The turn reached the model-round limit.";
            return result;
        }

        /// <summary>Prefix of <see cref="AgentRunResult.Detail"/> when a tool failed twice with the same error; the tool name follows.</summary>
        public const string RepeatedToolFailure = "repeated-tool-failure: ";

        /// <summary>Detail of a turn whose model stream ended without a finish event.</summary>
        public const string StreamInterrupted = "The model stream ended before a completed event.";

        private static readonly Regex ToolCallIdField = new Regex("\"tool_call_id\"\\s*:\\s*\"[^\"]*\",?", RegexOptions.CultureInvariant);

        /// <summary>The tool result without its per-call id, so the same error from two calls compares equal.</summary>
        private static string FailureText(string contentJson)
        {
            return ToolCallIdField.Replace(contentJson ?? string.Empty, string.Empty);
        }
    }

    public static class HistoryKey
    {
        public static string TurnId(IList<ChatMessage> history)
        {
            var count = 0;
            for (var i = 0; i < history.Count; i++)
            {
                if (history[i].Role == "user")
                {
                    count++;
                }
            }

            return "turn-" + count;
        }

        public static string ToolKey(string turnId, int round, string toolCallId)
        {
            return turnId + ":" + round + ":" + (toolCallId ?? string.Empty);
        }

        public static string ArgumentsHash(string json)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                for (var i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
