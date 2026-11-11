using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ArniLite.Api;
using ArniLite.Core;
using ArniLite.Storage;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ArniLite.AppTests
{
    public class ChatSessionTests
    {
        [Fact]
        public Task SwitchingChatsRestoresTheirProjectAndGeneralChatClearsIt() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            var session = fixture.CreateSession();
            Assert.Equal(fixture.ProjectB, session.ProjectPath);
            session.OpenChatCommand.Execute(fixture.ChatA);
            Assert.Equal(fixture.ProjectA, session.ProjectPath);
            session.NewChatCommand.Execute(null);
            Assert.False(session.HasProject);
            session.NewProjectChatCommand.Execute(fixture.ProjectB);
            Assert.Equal(fixture.ProjectB, session.ProjectPath);
            Assert.Contains(session.ChatRows, row => row.Title == "Новый чат" && row.ProjectPath == fixture.ProjectB);
            await Task.CompletedTask;
        });

        [Fact]
        public Task SearchFindsChatsInsideCollapsedProjects() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            var session = fixture.CreateSession();
            session.ToggleProjectCommand.Execute(fixture.ProjectA);
            Assert.DoesNotContain(session.ChatRows, row => row.ChatId == fixture.ChatA);
            session.Search = "альфа";
            Assert.Contains(session.ChatRows, row => row.ChatId == fixture.ChatA);
            Assert.DoesNotContain(session.ChatRows, row => row.ChatId == fixture.ChatB);
            session.Search = string.Empty;
            Assert.DoesNotContain(session.ChatRows, row => row.ChatId == fixture.ChatA);
            await Task.CompletedTask;
        });

        [Fact]
        public Task FilePreviewReadsTextWithoutCallingTheModelOrRunningAProcess() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            File.WriteAllText(Path.Combine(fixture.ProjectB, "sample.txt"), "Локальный просмотр", new UTF8Encoding(false));
            var session = fixture.CreateSession();
            session.OpenProjectFileCommand.Execute("sample.txt");
            Assert.True(session.HasFilePreview);
            Assert.Equal("Локальный просмотр", session.FilePreviewBody);
            session.ClosePreviewCommand.Execute(null);
            Assert.False(session.HasFilePreview);
            await Task.CompletedTask;
        });

        [Fact]
        public Task StopCancelsTheActiveStreamAndPersistsPartialTextOnce() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            var stream = new PausingStream();
            var handler = new StreamHandler(stream);
            using var client = new HttpClient(handler);
            var session = fixture.CreateSession(client);
            session.SelectedCatalog = new CatalogModel { Id = "local-test" };
            session.NewChatCommand.Execute(null);
            session.Draft = "Проверка отмены";
            var idle = new TaskCompletionSource<bool>();
            session.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatSession.IsBusy) && !session.IsBusy) idle.TrySetResult(true);
            };
            session.SendCommand.Execute(null);
            await WithTimeout(stream.Paused.Task);
            Assert.True(handler.SawTools);
            Assert.True(session.IsBusy);
            Assert.True(session.StopCommand.CanExecute(null));
            session.StopCommand.Execute(null);
            await WithTimeout(idle.Task);
            var messages = fixture.History.Load(fixture.History.CurrentChatId());
            Assert.Equal(new[] { "Часть ответа" }, messages.Where(message => message.Role == "assistant").Select(message => message.Content));
            Assert.Single(session.Items.Where(row => row.Title == "Arni" && row.Body == "Часть ответа"));
            Assert.False(session.StopCommand.CanExecute(null));
        });

        private static async Task WithTimeout(Task task)
        {
            Assert.Same(task, await Task.WhenAny(task, Task.Delay(5000)));
            await task;
        }

        [Fact]
        public Task LanguageSwitchUpdatesLiveBindingsAndAccountWithoutNetworkRequests() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            var handler = new AccountHandler();
            using var client = new HttpClient(handler);
            var session = fixture.CreateSession(client);
            await session.LoadAccountAsync();
            var label = (System.Windows.Controls.TextBlock)System.Windows.Markup.XamlReader.Parse(
                "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='clr-namespace:ArniLite;assembly=ArniLite' Text='{local:Loc Settings}' />");
            try
            {
                StringCatalog.SetLanguage("en");
                session.RefreshLocalization();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("Settings", label.Text);
                Assert.Equal("Free account", session.Account!.AccountType);
                Assert.Equal("free", session.Models[0].PriceLabel);
                StringCatalog.SetLanguage("kk");
                session.RefreshLocalization();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("Баптаулар", label.Text);
                Assert.Equal("Тегін аккаунт", session.Account!.AccountType);
                Assert.Equal("тегін", session.Models[0].PriceLabel);
                Assert.Equal("Задача бета", session.ChatTitle);
                Assert.Equal(1, handler.CatalogRequests);
                Assert.Equal(0, handler.ModelRequests);
            }
            finally { StringCatalog.SetLanguage("ru"); }
        });

        [Fact]
        public Task MediaPreviewDoesNotReadLargeFilesAsTextOrStartPlaybackAutomatically() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            var path = Path.Combine(fixture.ProjectB, "large.wav");
            using (var file = File.Create(path)) file.SetLength(2 * 1024 * 1024);
            var session = fixture.CreateSession();
            session.OpenProjectFileCommand.Execute("large.wav");
            Assert.True(session.HasMediaPreview);
            Assert.True(session.IsAudioPreview);
            Assert.False(session.HasTextPreview);
            Assert.Equal(string.Empty, session.FilePreviewBody);
            var view = new MediaPreviewControl { SourcePath = session.FilePreviewPath, IsAudio = true };
            var player = (System.Windows.Controls.MediaElement)view.FindName("Player");
            Assert.Null(player.Source);
            view.Release();
            Assert.Null(player.Source);
            session.ClosePreviewCommand.Execute(null);
            Assert.False(session.HasMediaPreview);
            Assert.Equal(string.Empty, session.FilePreviewPath);
            await Task.CompletedTask;
        });

        [Fact]
        public Task AccountLoadsWhenCatalogFailsAndRemainsVisibleOnRefreshFailure() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            var handler = new AccountHandler { FailCatalog = true };
            using var client = new HttpClient(handler);
            var session = fixture.CreateSession(client);
            await session.LoadAccountAsync();
            Assert.Equal("user@example.com", session.AccountTitle);
            Assert.Contains("Бесплатные шаги: 9 из 150", session.Account!.LimitLines);
            Assert.Empty(session.Models);
            handler.FailAccount = true;
            await session.RefreshAccountAsync();
            Assert.Equal("user@example.com", session.AccountTitle);
            Assert.Contains("Бесплатные шаги: 9 из 150", session.Account.LimitLines);
            Assert.Equal("Не удалось обновить лимиты. Нажмите «Обновить».", session.AccountError);
        });

        [Fact]
        public Task LimitsRefreshAfterTurnWithoutReloadingTheModelSelection() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            var handler = new AccountHandler();
            using var client = new HttpClient(handler);
            var session = fixture.CreateSession(client);
            await session.LoadAccountAsync();
            var selected = session.SelectedCatalog;
            var refreshed = new TaskCompletionSource<bool>();
            session.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatSession.Account) && session.Account!.LimitLines.Contains("Бесплатные шаги: 8 из 150")) refreshed.TrySetResult(true);
            };
            session.Draft = "Ответь коротко";
            session.SendCommand.Execute(null);
            await WithTimeout(refreshed.Task);
            Assert.Same(selected, session.SelectedCatalog);
            Assert.Equal(1, handler.CatalogRequests);
            Assert.Equal(1, handler.ModelRequests);
        });

        [Fact]
        public Task RejectedModelIsRemovedWithoutSilentlySwitchingOrRetrying() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            var handler = new AccountHandler { RejectModel = true };
            using var client = new HttpClient(handler);
            var session = fixture.CreateSession(client);
            await session.LoadAccountAsync();
            var rejected = session.SelectedCatalog.Id;
            var idle = new TaskCompletionSource<bool>();
            session.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatSession.IsBusy) && !session.IsBusy) idle.TrySetResult(true);
            };
            session.Draft = "Проверь";
            session.SendCommand.Execute(null);
            await WithTimeout(idle.Task);
            Assert.Null(session.SelectedCatalog);
            Assert.DoesNotContain(session.Models, model => model.Id == rejected);
            Assert.Contains(session.Items, row => row.Body == "Провайдер отклонил эту модель для Arni Lite. Выберите другую модель.");
            Assert.Equal(1, handler.ModelRequests);
            Assert.NotEqual("Новый чат", session.ChatRows.Single(row => row == session.SelectedEntry).Title);
        });

        [Fact]
        public Task ToolRoundDoesNotDuplicateStreamedMessages() => OnDispatcher(async () =>
        {
            using var fixture = new SessionFixture();
            File.WriteAllText(Path.Combine(fixture.ProjectB, "sample.txt"), "test");
            using var client = new HttpClient(new ToolRoundHandler());
            var session = fixture.CreateSession(client);
            session.SelectedCatalog = new CatalogModel { Id = "local-test" };
            session.Draft = "Прочитай sample.txt";
            var idle = new TaskCompletionSource<bool>();
            session.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatSession.IsBusy) && !session.IsBusy) idle.TrySetResult(true);
            };
            session.SendCommand.Execute(null);
            await WithTimeout(idle.Task);
            Assert.Equal(new[] { "Читаю файл.", "Файл прочитан." }, session.Items.Where(row => row.Title == "Arni").Select(row => row.Body));
        });

        private static Task OnDispatcher(Func<Task> action)
        {
            var completion = new TaskCompletionSource<bool>();
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await action(); completion.TrySetResult(true); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
                }));
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return completion.Task;
        }

        private sealed class SessionFixture : IDisposable
        {
            public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "arni-ui-tests-" + Guid.NewGuid().ToString("N"));
            public string ProjectA { get; }
            public string ProjectB { get; }
            public string ChatA { get; }
            public string ChatB { get; }
            public ChatHistory History { get; }

            public SessionFixture()
            {
                ProjectA = Path.Combine(DirectoryPath, "project-a");
                ProjectB = Path.Combine(DirectoryPath, "project-b");
                Directory.CreateDirectory(ProjectA);
                Directory.CreateDirectory(ProjectB);
                History = new ChatHistory(Path.Combine(DirectoryPath, "chats"));
                ChatA = History.NewChat();
                History.Append(ChatA, new ChatMessage { Role = "user", Content = "Задача альфа" });
                ChatB = History.NewChat();
                History.Append(ChatB, new ChatMessage { Role = "user", Content = "Задача бета" });
                File.WriteAllLines(Path.Combine(DirectoryPath, "chat-folders.txt"), new[] { ChatA + "\t" + ProjectA, ChatB + "\t" + ProjectB });
                File.WriteAllLines(Path.Combine(DirectoryPath, "folders.txt"), new[] { ProjectA, ProjectB });
            }

            public ChatSession CreateSession(HttpClient? client = null) => new ChatSession(
                new ArniApiClient(client ?? new HttpClient(new RejectNetwork())), new MemoryToken(), new OsFacts(), new RejectProcesses(), DirectoryPath);

            public void Dispose() => Directory.Delete(DirectoryPath, true);
        }

        private sealed class MemoryToken : ITokenStore
        {
            public string? Load() => "local-test-token";
            public void Save(string token) => throw new NotSupportedException();
            public void Clear() { }
        }

        private sealed class RejectProcesses : IProcessRunner
        {
            public Task<ProcessRunResult> RunPowerShellAsync(string command, int timeoutMs, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected process");
            public Task<ProcessRunResult> RunProcessAsync(string fileName, string arguments, string standardInput, string workingDirectory, int timeoutMs, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected process");
        }

        private sealed class RejectNetwork : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected network request");
        }

        private sealed class StreamHandler : HttpMessageHandler
        {
            private readonly Stream _stream;
            public bool SawTools { get; private set; }
            public StreamHandler(Stream stream) { _stream = stream; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Post)
                {
                    var payload = Newtonsoft.Json.Linq.JObject.Parse(await request.Content.ReadAsStringAsync());
                    SawTools = payload["tools"] is Newtonsoft.Json.Linq.JArray tools && tools.Count > 0;
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_stream) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return response;
            }
        }

        private sealed class AccountHandler : HttpMessageHandler
        {
            public bool FailCatalog { get; set; }
            public bool FailAccount { get; set; }
            public bool RejectModel { get; set; }
            public int CatalogRequests { get; private set; }
            public int ModelRequests { get; private set; }
            private int _remaining = 9;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath == "/api/account")
                    return Json(FailAccount ? "{\"error\":\"unavailable\"}" : "{\"user\":{\"email\":\"user@example.com\",\"plan\":\"FREE\",\"budget\":null,\"freeSteps\":{\"remaining\":" + _remaining + ",\"limit\":150}}}", FailAccount ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
                if (request.RequestUri.AbsolutePath == "/api/models")
                {
                    CatalogRequests++;
                    return Json(FailCatalog ? "{\"error\":\"unavailable\"}" : "{\"data\":[{\"id\":\"test/model-a\",\"arni_free\":true},{\"id\":\"test/model-b\",\"arni_free\":true}]}", FailCatalog ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
                }
                ModelRequests++;
                if (RejectModel) return Json("{\"error\":\"test/model-a is only available on agentic harnesses.\"}", HttpStatusCode.Forbidden);
                _remaining--;
                return Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Готово.\"},\"finish_reason\":\"stop\"}]}");
            }

            private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK) => Task.FromResult(
                new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private sealed class ToolRoundHandler : HttpMessageHandler
        {
            private int _requests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = _requests++ == 0
                    ? "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Читаю файл.\",\"tool_calls\":[{\"id\":\"read-1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"sample.txt\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}"
                    : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Файл прочитан.\"},\"finish_reason\":\"stop\"}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }

        private sealed class PausingStream : Stream
        {
            private readonly byte[] _chunk = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Часть ответа\"}}]}\n\n");
            private bool _sent;
            public TaskCompletionSource<bool> Paused { get; } = new TaskCompletionSource<bool>();
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (!_sent)
                {
                    _sent = true;
                    Array.Copy(_chunk, 0, buffer, offset, _chunk.Length);
                    return _chunk.Length;
                }
                Paused.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
