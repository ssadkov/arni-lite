using ArniLite.Core;
using Xunit;

namespace ArniLite.UnitTests
{
    public class ReadmeTextTests
    {
        [Fact]
        public void GitHubWinsWhenItIsMarkdown()
        {
            Assert.Equal("# Arni", ReadmeText.Prefer("# Arni", "# Local", "# Embedded"));
        }

        [Fact]
        public void LocalReplacesAMissingGitHubPage()
        {
            Assert.Equal("# Local", ReadmeText.Prefer("404: Not Found", "# Local", "# Embedded"));
        }

        [Fact]
        public void EmbeddedRemainsWhenNothingElseIsReadable()
        {
            Assert.Equal("# Embedded", ReadmeText.Prefer("<html>missing</html>", "   ", "# Embedded"));
        }
    }
}
