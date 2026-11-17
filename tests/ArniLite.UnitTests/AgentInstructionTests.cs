using System;
using System.IO;
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
    public sealed class AgentInstructionTests
    {
        [Fact]
        public void AttachedImageBecomesImageUrlAndTextStaysText()
        {
            var text = ChatWire.Content(new ChatMessage { Content = "смотри" });
            Assert.Equal("смотри", (string)text);
            var parts = ChatWire.Content(new ChatMessage
            {
                Content = "смотри",
                Images = new System.Collections.Generic.List<ChatImagePart>
                {
                    new ChatImagePart { MediaType = "image/jpeg", DataBase64 = "aaaa" },
                },
            }) as JArray;
            Assert.Equal("text", (string)parts[0]["type"]);
            Assert.Equal("image_url", (string)parts[1]["type"]);
            Assert.Equal("data:image/jpeg;base64,aaaa", (string)parts[1]["image_url"]["url"]);
            Assert.True(AttachmentText.IsBinary(new byte[] { 1, 0, 2 }));
            var clipped = AttachmentText.Clip(new string('а', AttachmentText.MaxChars + 5), out var truncated);
            Assert.True(truncated);
            Assert.Equal(AttachmentText.MaxChars, clipped.Length);
            Assert.Contains("notes.txt:", AttachmentText.Compose("вопрос", "notes.txt", "текст", false));
        }

        [Fact]
        public void ReadsRootAgentsAndTruncates()
        {
            var root = Path.Combine(Path.GetTempPath(), "arni-agents-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Assert.Equal(string.Empty, AgentRules.Read(root));
                File.WriteAllText(root + "\\AGENTS.md", "  держи тесты зелёными  ");
                var text = AgentRules.Read(root);
                Assert.StartsWith("AGENTS.md:\n", text);
                Assert.Contains("держи тесты зелёными", text);
                File.WriteAllText(root + "\\AGENTS.md", new string('я', AgentRules.MaxChars + 25));
                Assert.Equal(AgentRules.MaxChars, AgentRules.Read(root).Substring("AGENTS.md:\n".Length).TrimEnd().Length);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void ProjectSkillOverridesBuiltIn()
        {
            var root = Path.Combine(Path.GetTempPath(), "arni-skills-" + Guid.NewGuid().ToString("N"));
            var skillDir = Path.Combine(root, ".github", "skills", "source-research");
            Directory.CreateDirectory(skillDir);
            Directory.CreateDirectory(Path.Combine(root, ".github", "skills", "Bad_Name"));
            try
            {
                File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: source-research\ndescription: Проектный вариант проверки источников.\n---\nСначала смотри локальные файлы.\n");
                File.WriteAllText(Path.Combine(root, ".github", "skills", "Bad_Name", "SKILL.md"), "---\nname: bad_name\ndescription: Нельзя.\n---\nНет.\n");
                var skills = SkillCatalog.List(root);
                var research = skills.Single(item => item.Name == "source-research");
                Assert.True(research.FromProject);
                Assert.Contains("Проектный вариант", research.Description);
                Assert.DoesNotContain(skills, item => item.Name == "bad_name");
                Assert.Contains("Сначала смотри локальные файлы.", SkillCatalog.Read(root, "source-research"));
                Assert.Contains("file-organizing", SkillCatalog.Prompt(root));
                Assert.Equal(string.Empty, SkillCatalog.Read(root, "../source-research"));
                var tool = ToolSchemas.AgentTools().Single(item => (string)item["function"]["name"] == "read_skill");
                Assert.Equal("name", (string)tool["function"]["parameters"]["required"][0]);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void RejectsMcpAddressWithLogin()
        {
            Assert.Equal(string.Empty, McpAddress.Normalize("http://mcp.vkusvill.ru/mcp"));
            Assert.Equal(string.Empty, McpAddress.Normalize("https://user:secret@mcp.vkusvill.ru/mcp"));
            Assert.EndsWith("/mcp", McpAddress.Normalize("https://mcp.vkusvill.ru/mcp"));
            Assert.Equal("mcp.vkusvill.ru", McpAddress.Host("https://mcp.vkusvill.ru/mcp"));
        }

        [Fact]
        public void MigratesOneMcpUrlAndKeepsTokensOutOfPreferences()
        {
            var root = Path.Combine(Path.GetTempPath(), "arni-mcp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "preferences.json");
            try
            {
                File.WriteAllText(path, "{\"McpUrl\":\"https://mcp.vkusvill.ru/mcp\"}");
                var store = new ArniLite.Storage.PreferencesStore(path);
                Assert.Single(store.Value.McpServers);
                Assert.Equal("mcp.vkusvill.ru", McpAddress.Host(store.Value.McpServers[0].Url));
                Assert.Equal(string.Empty, store.Value.McpUrl);
                var saved = File.ReadAllText(path);
                Assert.DoesNotContain("McpUrl\":\"https", saved);
                var again = new ArniLite.Storage.PreferencesStore(path);
                Assert.Single(again.Value.McpServers);
                Assert.Equal(store.Value.McpServers[0].Id, again.Value.McpServers[0].Id);
                var map = ArniLite.Storage.McpSecretMap.Upsert("{}", store.Value.McpServers[0].Id, "tok\"en");
                Assert.False(ArniLite.Storage.McpSecretMap.IsDocument("Bearer secret"));
                Assert.Equal("tok\"en", ArniLite.Storage.McpSecretMap.Read(map, store.Value.McpServers[0].Id));
                Assert.True(ArniLite.Storage.McpSecretMap.IsEmpty(ArniLite.Storage.McpSecretMap.Remove(map, store.Value.McpServers[0].Id)));
                Assert.DoesNotContain("tok", saved);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task OpensRemoteToolsAndKeepsText()
        {
            using var http = new HttpClient(new VkusVillHandler());
            var session = await McpClient.OpenAsync(http, "https://mcp.vkusvill.ru/mcp", "secret", CancellationToken.None);
            Assert.Single(session.Tools);
            Assert.Equal("vkusvill_products_search", session.Tools[0].Name);
            var called = await session.CallAsync("vkusvill_products_search", "{\"q\":\"сыр\"}", CancellationToken.None);
            Assert.False(called.IsError);
            Assert.Contains("сыр", called.Text);
            Assert.DoesNotContain("aaaa", called.Text);
            await session.CloseAsync();
        }

        [Fact]
        public async Task TwoServersRenameOnlyTheCollidingTool()
        {
            using var http = new HttpClient(new TwoMcpHandler());
            var first = await McpClient.OpenAsync(http, "https://one.example/mcp", string.Empty, CancellationToken.None);
            var second = await McpClient.OpenAsync(http, "https://two.example/mcp", string.Empty, CancellationToken.None);
            var bundle = McpBundle.Combine(new[] { first, second });
            var tools = bundle.ToOpenAiTools();
            Assert.Equal(2, tools.Count);
            Assert.Equal("lookup", (string)tools[0]["function"]["name"]);
            Assert.Equal("two_example_lookup", (string)tools[1]["function"]["name"]);
            Assert.Contains("уже подключены", bundle.PromptLine());
            var called = await bundle.CallAsync("two_example_lookup", "{}", CancellationToken.None);
            Assert.Contains("two.example", called.Text);
            await bundle.CloseAsync();
        }

        private sealed class VkusVillHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Delete)
                {
                    Assert.Equal("sess-1", request.Headers.GetValues("Mcp-Session-Id").Single());
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                var json = JObject.Parse(await request.Content.ReadAsStringAsync());
                var method = (string)json["method"];
                Assert.Equal("Bearer secret", request.Headers.GetValues("Authorization").Single());
                if (method == "initialize")
                {
                    var response = Json(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = json["id"],
                        ["result"] = new JObject
                        {
                            ["protocolVersion"] = "2025-03-26",
                            ["capabilities"] = new JObject(),
                            ["serverInfo"] = new JObject { ["name"] = "vkusvill", ["version"] = "1" },
                        },
                    });
                    response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "sess-1");
                    return response;
                }

                Assert.Equal("sess-1", request.Headers.GetValues("Mcp-Session-Id").Single());
                if (method == "notifications/initialized")
                {
                    Assert.Null(json["id"]);
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                }

                if (method == "tools/list")
                {
                    return Json(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = json["id"],
                        ["result"] = new JObject
                        {
                            ["tools"] = new JArray
                            {
                                new JObject { ["name"] = "read_file", ["description"] = "clash", ["inputSchema"] = new JObject { ["type"] = "object" } },
                                new JObject { ["name"] = "vkusvill_products_search", ["description"] = "Поиск товаров", ["inputSchema"] = new JObject { ["type"] = "object" } },
                            },
                        },
                    });
                }

                var sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":" + json["id"] + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"сыр\"},{\"type\":\"image\",\"data\":\"aaaa\"}],\"isError\":false}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                };
            }

            private static HttpResponseMessage Json(JObject body)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
                };
            }
        }

        private sealed class TwoMcpHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var host = request.RequestUri.Host;
                if (request.Method == HttpMethod.Delete)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                var json = JObject.Parse(await request.Content.ReadAsStringAsync());
                var method = (string)json["method"];
                if (method == "initialize")
                {
                    var response = Json(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = json["id"],
                        ["result"] = new JObject
                        {
                            ["protocolVersion"] = "2025-03-26",
                            ["capabilities"] = new JObject(),
                            ["serverInfo"] = new JObject { ["name"] = host, ["version"] = "1" },
                            ["instructions"] = host,
                        },
                    });
                    response.Headers.TryAddWithoutValidation("Mcp-Session-Id", host);
                    return response;
                }

                if (method == "notifications/initialized")
                {
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                }

                if (method == "tools/list")
                {
                    return Json(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = json["id"],
                        ["result"] = new JObject
                        {
                            ["tools"] = new JArray
                            {
                                new JObject { ["name"] = "lookup", ["description"] = host, ["inputSchema"] = new JObject { ["type"] = "object" } },
                            },
                        },
                    });
                }

                Assert.Equal("lookup", (string)json["params"]["name"]);
                return Json(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = json["id"],
                    ["result"] = new JObject { ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = host } } },
                });
            }

            private static HttpResponseMessage Json(JObject body)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
                };
            }
        }
    }
}
