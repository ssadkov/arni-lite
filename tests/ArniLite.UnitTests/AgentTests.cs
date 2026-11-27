using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Storage;
using Xunit;

namespace ArniLite.UnitTests
{
    public class ToolCallAssemblerTests
    {
        [Fact]
        public void JoinsSplitArgumentsAndKeepsNonSequentialIndexes()
        {
            var assembler = new ToolCallAssembler();
            assembler.Add(2, "call-b", "run_command", "{\"command\":");
            assembler.Add(0, "call-a", "read_file", "{\"path\":\"a.txt\"}");
            assembler.Add(2, string.Empty, string.Empty, "\"dir\"}");
            Assert.True(assembler.TryComplete(out var calls, out var error), error);
            Assert.Equal(2, calls.Count);
            Assert.Equal("call-a", calls[0].Id);
            Assert.Equal("call-b", calls[1].Id);
            Assert.Equal("{\"command\":\"dir\"}", calls[1].ArgumentsJson);
        }

        [Fact]
        public void AttachesArgumentsThatArriveUnderAnotherIndex()
        {
            var assembler = new ToolCallAssembler();
            assembler.Add(0, "call-image", "generate_image", string.Empty);
            assembler.Add(1, string.Empty, string.Empty, "{\"prompt\":\"a red kite\",\"save_path\":\"hero.png\"}");
            Assert.True(assembler.TryComplete(out var calls, out var error), error);
            var call = Assert.Single(calls);
            Assert.Equal("generate_image", call.Name);
            Assert.Contains("hero.png", call.ArgumentsJson);
        }

        [Fact]
        public void RejectsIncompleteJson()
        {
            var assembler = new ToolCallAssembler();
            assembler.Add(0, "call-a", "read_file", "{\"path\":");
            Assert.False(assembler.TryComplete(out _, out var error));
            Assert.Contains("JSON", error);
        }
    }

    public class WorkspacePathTests
    {
        [Fact]
        public void RejectsTraversalPrefixCollisionAndUnc()
        {
            var root = Path.Combine(Path.GetTempPath(), "arni-lite-proj");
            Directory.CreateDirectory(root);
            Assert.True(WorkspacePath.TryCombine(root, "src\\file.txt", out var inside, out _));
            Assert.StartsWith(root, inside, System.StringComparison.OrdinalIgnoreCase);
            Assert.False(WorkspacePath.TryCombine(root, "..\\outside.txt", out _, out _));
            Assert.False(WorkspacePath.TryCombine(root, "C:\\Windows\\notepad.exe", out _, out _));
            Assert.False(WorkspacePath.TryCombine(root, "file.txt:stream", out _, out _));
            Assert.False(WorkspacePath.TryCombine("\\\\server\\share", "a.txt", out _, out _));
            var sibling = root + "-other";
            Assert.False(WorkspacePath.TryCombine(root, "..\\" + Path.GetFileName(sibling) + "\\secret.txt", out _, out _));
        }
    }

    public class AgentOrchestratorTests
    {
        [Fact]
        public async Task DoesNotRunToolsWhenTheStreamIsInterrupted()
        {
            var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "hi" } };
            var tools = new RecordingTools();
            var result = await new AgentOrchestrator(new SystemClock(), new AgentLimits()).RunAsync(
                history,
                true,
                new ScriptedModel(new ModelTurn { Content = "partial", Interrupted = true }),
                tools,
                _ => { },
                CancellationToken.None);
            Assert.Equal("interrupted", result.Status);
            Assert.Equal(0, tools.Calls);
        }

        [Fact]
        public async Task StopsARepeatedSideEffectAfterDenial()
        {
            var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "run" } };
            var call = new ToolCallRecord { Index = 0, Id = "call-1", Name = "run_command", ArgumentsJson = "{\"command\":\"dir\"}" };
            var model = new ScriptedModel(
                new ModelTurn { Done = true, FinishReason = "tool_calls", ToolCalls = new List<ToolCallRecord> { call } },
                new ModelTurn { Done = true, FinishReason = "tool_calls", ToolCalls = new List<ToolCallRecord> { call } });
            var tools = new RecordingTools { Result = new ToolExecution { Status = "denied", SideEffect = true, ContentJson = "{\"status\":\"denied\"}" } };
            var result = await new AgentOrchestrator(new SystemClock(), new AgentLimits()).RunAsync(
                history, true, model, tools, _ => { }, CancellationToken.None);
            Assert.Equal(1, tools.Calls);
            Assert.Contains("repeated", result.Detail);
        }

        [Fact]
        public async Task StopsWhenAToolReturnsTheSameErrorTwice()
        {
            var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "tasks today" } };
            var first = new ToolCallRecord { Index = 0, Id = "call-1", Name = "list_tasks", ArgumentsJson = "{\"date\":\"today\",\"tz\":{\"name\":\"UTC\"}}" };
            var second = new ToolCallRecord { Index = 0, Id = "call-2", Name = "list_tasks", ArgumentsJson = "{\"tz\":{\"name\":\"UTC\"},\"date\":\"today\"}" };
            var model = new ScriptedModel(
                new ModelTurn { Done = true, FinishReason = "tool_calls", ToolCalls = new List<ToolCallRecord> { first } },
                new ModelTurn { Done = true, FinishReason = "tool_calls", ToolCalls = new List<ToolCallRecord> { second } },
                new ModelTurn { Done = true, FinishReason = "tool_calls", ToolCalls = new List<ToolCallRecord> { first } });
            var tools = new EchoingFailureTools("tz: input should be a valid string");
            var result = await new AgentOrchestrator(new SystemClock(), new AgentLimits()).RunAsync(
                history, true, model, tools, _ => { }, CancellationToken.None);
            Assert.Equal(new object[] { "failed", AgentOrchestrator.RepeatedToolFailure + "list_tasks", 2 }, new object[] { result.Status, result.Detail, tools.Calls });
        }

        [Fact]
        public async Task KeepsGoingWhenAToolFailsWithDifferentErrors()
        {
            var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "read" } };
            var call = new ToolCallRecord { Index = 0, Id = "call-1", Name = "read_file", ArgumentsJson = "{\"path\":\"a.txt\"}" };
            var model = new ScriptedModel(
                new ModelTurn { Done = true, FinishReason = "tool_calls", ToolCalls = new List<ToolCallRecord> { call } },
                new ModelTurn { Done = true, FinishReason = "tool_calls", ToolCalls = new List<ToolCallRecord> { call } },
                new ModelTurn { Done = true, FinishReason = "stop", Content = "done" });
            var tools = new EchoingFailureTools("missing a.txt", "locked a.txt");
            var result = await new AgentOrchestrator(new SystemClock(), new AgentLimits()).RunAsync(
                history, true, model, tools, _ => { }, CancellationToken.None);
            Assert.Equal(new object[] { "completed", 2 }, new object[] { result.Status, tools.Calls });
        }
    }

    public class JournalTests
    {
        [Fact]
        public void RestartDoesNotReplayAnExecutingAction()
        {
            var directory = Path.Combine(Path.GetTempPath(), "arni-lite-journal-" + Path.GetRandomFileName());
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "side-effects.jsonl");
            var journal = new JsonlJournal(path);
            journal.Append(new SideEffectEntry { Key = "turn-1:1:call", ArgumentsHash = "abc", State = "executing" });
            journal.MarkExecutingAsUnknown();
            Assert.False(SideEffectGuard.TryPrepare(journal, "turn-1:1:call", "abc", out var status, out _));
            Assert.Equal("outcome_unknown", status);
        }
    }

    public class CodeFenceTests
    {
        [Fact]
        public void SplitsACodeBlockFromProse()
        {
            var parts = CodeFences.Split("See\n```txt\nline\n```\nDone");
            Assert.Equal(3, parts.Count);
            Assert.False(parts[0].IsCode);
            Assert.True(parts[1].IsCode);
            Assert.Equal("line", parts[1].Text);
            Assert.Contains("Done", parts[2].Text);
        }

        [Fact]
        public void CopiesTheWholeAnswerAndACodeBlockSeparately()
        {
            var message = "See\n```txt\nline\n```\nDone";
            var parts = CodeFences.Split(message);
            Assert.Equal(message, ResponseCopy.Text(message, parts[0].IsCode, parts[0].Text));
            Assert.Equal("line", ResponseCopy.Text(message, parts[1].IsCode, parts[1].Text));
            Assert.Equal(message, ResponseCopy.Text(message, parts[2].IsCode, parts[2].Text));
        }
    }

    internal sealed class ScriptedModel : IModelCompletion
    {
        private readonly Queue<ModelTurn> _turns = new Queue<ModelTurn>();

        public ScriptedModel(params ModelTurn[] turns)
        {
            foreach (var turn in turns)
            {
                _turns.Enqueue(turn);
            }
        }

        public Task<ModelTurn> CompleteAsync(IList<ChatMessage> messages, bool withTools, System.Action<string> onDelta, CancellationToken cancellationToken)
        {
            return Task.FromResult(_turns.Dequeue());
        }
    }

    internal sealed class RecordingTools : IToolExecutor
    {
        public int Calls { get; private set; }
        public ToolExecution Result { get; set; } = new ToolExecution { Status = "completed", ContentJson = "{}" };

        public Task<ToolExecution> ExecuteAsync(string turnId, int round, ToolCallRecord call, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    /// <summary>Fails every call, echoing its call id like the real executor, with the given errors in turn (the last one repeats).</summary>
    internal sealed class EchoingFailureTools : IToolExecutor
    {
        private readonly string[] _errors;

        public EchoingFailureTools(params string[] errors)
        {
            _errors = errors;
        }

        public int Calls { get; private set; }

        public Task<ToolExecution> ExecuteAsync(string turnId, int round, ToolCallRecord call, CancellationToken cancellationToken)
        {
            var error = _errors[System.Math.Min(Calls, _errors.Length - 1)];
            Calls++;
            return Task.FromResult(new ToolExecution
            {
                Status = "failed",
                ContentJson = "{\"schema_version\":1,\"tool_call_id\":\"" + call.Id + "\",\"status\":\"failed\",\"result\":{\"text\":\"" + error + "\"}}",
            });
        }
    }
}
