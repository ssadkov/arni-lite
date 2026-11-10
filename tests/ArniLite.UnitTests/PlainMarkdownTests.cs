using ArniLite.Core;
using Xunit;

namespace ArniLite.UnitTests
{
    public class PlainMarkdownTests
    {
        [Fact]
        public void AlignsPipeTableAndStripsMarkers()
        {
            var blocks = PlainMarkdown.Format("| A | B |\n| --- | --- |\n| **one** | `two` |");
            var table = Assert.Single(blocks);
            Assert.Equal(MarkdownKind.Table, table.Kind);
            Assert.Equal("A    B\n---  ---\none  two", table.Plain);
            Assert.Contains(table.Runs, run => run.Bold && run.Text == "A");
            Assert.Contains(table.Runs, run => run.Bold && run.Text == "one");
            Assert.Contains(table.Runs, run => !run.Bold && run.Text == "two");
        }

        [Fact]
        public void FormatsHeadingsBoldAndRules()
        {
            var blocks = PlainMarkdown.Format("### Place\n\n- **Free:** 10\n\n---\n\nSay **hi** there");
            Assert.Equal(MarkdownKind.Heading, blocks[0].Kind);
            Assert.Equal(3, blocks[0].HeadingLevel);
            Assert.Equal("Place", blocks[0].Plain);
            Assert.Equal(MarkdownKind.Blank, blocks[1].Kind);
            Assert.Equal("- Free: 10", blocks[2].Plain);
            Assert.Contains(blocks[2].Runs, run => run.Bold && run.Text == "Free:");
            Assert.Equal(MarkdownKind.Rule, blocks[4].Kind);
            Assert.Equal("Say hi there", blocks[6].Plain);
            Assert.Contains(blocks[6].Runs, run => run.Bold && run.Text == "hi");
        }

        [Fact]
        public void LeavesASinglePipeLineAsText()
        {
            var blocks = PlainMarkdown.Format("a | b | c");
            var paragraph = Assert.Single(blocks);
            Assert.Equal(MarkdownKind.Paragraph, paragraph.Kind);
            Assert.Equal("a | b | c", paragraph.Plain);
        }
    }
}
