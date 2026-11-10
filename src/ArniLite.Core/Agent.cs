using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArniLite.Core
{
    public sealed class ChatMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string ToolCallId { get; set; } = string.Empty;
        public List<ToolCallRecord> ToolCalls { get; set; } = new List<ToolCallRecord>();
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
                    result.Detail = "The model stream ended before a completed event.";
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
                }
            }

            result.Status = "failed";
            result.Detail = "The turn reached the model-round limit.";
            return result;
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
