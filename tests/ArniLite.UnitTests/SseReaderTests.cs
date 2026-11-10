using System.Text;
using ArniLite.Api;
using Xunit;

namespace ArniLite.UnitTests
{
    public class SseReaderTests
    {
        [Fact]
        public void SplitsUtf8AcrossChunks()
        {
            var reader = new SseReader();
            var bytes = Encoding.UTF8.GetBytes("data: проверка\n\n");
            Assert.Empty(reader.Push(bytes, 7));
            var rest = new byte[bytes.Length - 7];
            System.Buffer.BlockCopy(bytes, 7, rest, 0, rest.Length);
            var only = Assert.Single(reader.Push(rest, rest.Length));
            Assert.Equal("проверка", only.Data);
            Assert.Equal(string.Empty, reader.Remainder);
        }

        [Fact]
        public void KeepsJsonSplitUntilTheDelimiter()
        {
            var reader = new SseReader();
            var first = Encoding.UTF8.GetBytes("data: {\"a\":");
            var second = Encoding.UTF8.GetBytes("1}\n\n");
            Assert.Empty(reader.Push(first, first.Length));
            Assert.Equal("{\"a\":1}", Assert.Single(reader.Push(second, second.Length)).Data);
        }

        [Fact]
        public void AcceptsCrlfCommentsAndSeveralEvents()
        {
            var payload = ": keep\r\ndata: one\r\ndata: two\r\n\r\ndata: three\n\n";
            var events = new SseReader().Push(Encoding.UTF8.GetBytes(payload), Encoding.UTF8.GetByteCount(payload));
            Assert.Equal(2, events.Count);
            Assert.Equal("one\ntwo", events[0].Data);
            Assert.Equal("three", events[1].Data);
        }

        [Fact]
        public void DoesNotInventAnEventWhenTheStreamEndsEarly()
        {
            var reader = new SseReader();
            var events = reader.Push(Encoding.UTF8.GetBytes("data: partial"), 13);
            Assert.Empty(events);
            Assert.Contains("partial", reader.Remainder);
        }
    }

    public class ChatStreamParserTests
    {
        [Fact]
        public void ReadsContentDoneAndBothErrorShapes()
        {
            Assert.Equal("ok", ChatStreamParser.Parse(new SseEvent("message", "{\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}")).Content);
            Assert.True(ChatStreamParser.Parse(new SseEvent("message", "[DONE]")).Done);
            Assert.Equal("bad", ChatStreamParser.Parse(new SseEvent("message", "{\"error\":\"bad\"}")).Error);
            Assert.Equal("no budget", ChatStreamParser.Parse(new SseEvent("message", "{\"error\":{\"code\":\"budget\",\"message\":\"no budget\"}}")).Error);
            Assert.True(ChatStreamParser.Parse(new SseEvent("message", "{")).Malformed);
        }
    }
}
