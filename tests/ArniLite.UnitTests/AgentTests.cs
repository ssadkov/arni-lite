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
}
