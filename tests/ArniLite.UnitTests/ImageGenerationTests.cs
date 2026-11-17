using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Api;
using ArniLite.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ArniLite.UnitTests
{
    public class ImageGenerationTests
    {
        [Fact]
        public void KeepsPngInsideTheProjectAndBuildsAStableKey()
        {
            Assert.True(ImageAsset.TryTarget(@"C:\work\app", "public/generated/hero.png", out var full, out _));
            Assert.EndsWith(@"public\generated\hero.png", full);
            Assert.False(ImageAsset.TryTarget(@"C:\work\app", @"C:\other\hero.png", out _, out _));
            Assert.False(ImageAsset.TryTarget(@"C:\work\app", @"..\hero.png", out _, out _));
            Assert.False(ImageAsset.TryTarget(@"C:\work\app", "notes.txt", out _, out _));
            Assert.Equal("16:9", ImageAsset.NormalizeRatio(null));
            Assert.Null(ImageAsset.NormalizeRatio("4:3"));
            Assert.True(ImageAsset.HasUnsupportedScript("кот"));
            var key = ImageAsset.RequestKey(@"C:\work\app", "public/generated/hero.png", " a red kite ", "16:9");
            Assert.Equal(64, key.Length);
            Assert.Equal(key, ImageAsset.RequestKey(@"C:\work\app", "public/generated/hero.png", "a red kite", "16:9"));
        }

        [Fact]
        public void AcceptsOnlyARealSizedPng()
        {
            var png = new byte[24];
            png[0] = 0x89;
            png[1] = 0x50;
            png[2] = 0x4E;
            png[3] = 0x47;
            png[4] = 0x0D;
            png[5] = 0x0A;
            png[6] = 0x1A;
            png[7] = 0x0A;
            Assert.True(ImageAsset.IsPng(png));
            png[0] = 0;
            Assert.False(ImageAsset.IsPng(png));
        }

        [Fact]
        public void ExposesGenerateImageToTheAgent()
        {
            var tool = ToolSchemas.AgentTools().Single(item => (string)item["function"]["name"] == "generate_image");
            var parameters = tool["function"]["parameters"];
            Assert.Equal("prompt", (string)parameters["required"][0]);
            Assert.Equal("save_path", (string)parameters["required"][1]);
            Assert.False((bool)parameters["additionalProperties"]);
        }

        [Fact]
        public void RendersTheSavedPathAsALink()
        {
            var blocks = PlainMarkdown.Format("Готово: [hero.png](C:\\work\\app\\hero.png)");
            var link = Assert.Single(blocks).Runs.Single(run => run.Link.Length > 0);
            Assert.Equal("hero.png", link.Text);
            Assert.Equal(@"C:\work\app\hero.png", link.Link);
        }

        [Fact]
        public async Task DownloadsThePngWithTheSameIdempotencyKey()
        {
            var png = new byte[24];
            png[0] = 0x89;
            png[1] = 0x50;
            png[2] = 0x4E;
            png[3] = 0x47;
            png[4] = 0x0D;
            png[5] = 0x0A;
            png[6] = 0x1A;
            png[7] = 0x0A;
            using var http = new HttpClient(new ImageHandler(png));
            var image = await new ArniApiClient(http).GenerateImageAsync("token", "a red kite", "16:9", new string('a', 64), CancellationToken.None);
            Assert.Equal("img-1", image.Id);
            Assert.Equal(40000, image.CostMicros);
            Assert.Equal(png, image.Png);
        }

        [Fact]
        public async Task KeepsFiveHttpResultsFromWebSearch()
        {
            const string body = "{\"results\":[{\"title\":\"Agent\",\"url\":\"https://example.com/a\",\"snippet\":\"From Exa\"},{\"title\":\"Skip\",\"url\":\"file:///secret\",\"snippet\":\"no\"},{\"title\":\"\",\"url\":\"http://example.com/b\",\"snippet\":\"second\"}]}";
            using var http = new HttpClient(new JsonHandler(body));
            var results = await new ArniApiClient(http).SearchWebAsync("token", "agent tools", CancellationToken.None);
            Assert.Equal(2, results.Count);
            Assert.Equal("https://example.com/a", (string)results[0]["url"]);
            Assert.Equal("http://example.com/b", (string)results[1]["title"]);
            var tool = ToolSchemas.AgentTools().Single(item => (string)item["function"]["name"] == "web_search");
            Assert.Equal("query", (string)tool["function"]["parameters"]["required"][0]);
        }

        private sealed class JsonHandler : HttpMessageHandler
        {
            private readonly string _body;
            public JsonHandler(string body) { _body = body; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("/api/web-search", request.RequestUri.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                });
            }
        }

        private sealed class ImageHandler : HttpMessageHandler
        {
            private readonly byte[] _png;
            public ImageHandler(byte[] png) { _png = png; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal(new string('a', 64), request.Headers.GetValues("Idempotency-Key").Single());
                    var body = new JObject { ["id"] = "img-1", ["model"] = "stability.stable-image-core-v1:1", ["costMicros"] = 40000 }.ToString();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_png),
                });
            }
        }
    }
}
