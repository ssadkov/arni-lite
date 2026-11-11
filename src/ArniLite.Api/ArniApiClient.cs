using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Core;
using Newtonsoft.Json.Linq;

namespace ArniLite.Api
{
    public sealed class ApiCallException : Exception
    {
        public ApiCallException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }

    public sealed class DesktopAuthStart
    {
        public string DeviceCode { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string UserCode { get; set; } = string.Empty;
        public string VerificationUri { get; set; } = string.Empty;
        public int ExpiresIn { get; set; }
        public int IntervalSeconds { get; set; } = 5;
    }

    public sealed class DesktopAuthPoll
    {
        public string Status { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public int IntervalSeconds { get; set; } = 5;
    }

    public sealed class ArniApiClient
    {
        private readonly HttpClient _http;

        public ArniApiClient(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public async Task<DiagnosticItem> ProbeTlsAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _http.GetAsync(ArniEndpoints.ApiOrigin + "/api/models", cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status == 200 || status == 401 || status == 403)
                {
                    return new DiagnosticItem("tls", "pass", "HTTPS to api.arnion.ru returned " + status + ".");
                }

                return new DiagnosticItem("tls", "fail", "HTTPS reached api.arnion.ru but returned " + status + ".");
            }
            catch (Exception ex)
            {
                return new DiagnosticItem("tls", "fail", ex.GetType().Name + ": " + ex.Message);
            }
        }

        public async Task<DesktopAuthStart> StartDesktopAuthAsync(CancellationToken cancellationToken)
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(ArniEndpoints.ApiOrigin + "/api/desktop/auth/start", content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(body, (int)response.StatusCode));
            }

            var json = JObject.Parse(body);
            var start = new DesktopAuthStart
            {
                DeviceCode = (string?)json["device_code"] ?? string.Empty,
                RequestId = (string?)json["request_id"] ?? string.Empty,
                UserCode = (string?)json["user_code"] ?? string.Empty,
                VerificationUri = (string?)json["verification_uri"] ?? string.Empty,
                ExpiresIn = (int?)json["expires_in"] ?? 0,
                IntervalSeconds = (int?)json["interval"] ?? 5,
            };
            if (string.IsNullOrEmpty(start.DeviceCode) || string.IsNullOrEmpty(start.VerificationUri))
            {
                throw new ApiCallException((int)response.StatusCode, "Sign-in response is incomplete.");
            }

            AuthLink.EnsureSafe(start.VerificationUri);
            if (start.VerificationUri.IndexOf(start.DeviceCode, StringComparison.Ordinal) >= 0)
            {
                throw new ApiCallException((int)response.StatusCode, "Sign-in link contains the device secret.");
            }

            return start;
        }

        public async Task<DesktopAuthPoll> PollDesktopAuthAsync(string deviceCode, CancellationToken cancellationToken)
        {
            using var content = new StringContent(
                new JObject { ["device_code"] = deviceCode }.ToString(Newtonsoft.Json.Formatting.None),
                Encoding.UTF8,
                "application/json");
            using var response = await _http.PostAsync(ArniEndpoints.ApiOrigin + "/api/desktop/auth/token", content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(body, (int)response.StatusCode));
            }

            var json = JObject.Parse(body);
            return new DesktopAuthPoll
            {
                Status = (string?)json["status"] ?? string.Empty,
                Token = (string?)json["token"] ?? string.Empty,
                IntervalSeconds = (int?)json["interval"] ?? 5,
            };
        }

        public async Task<string> StreamOnceAsync(string token, string model, string chatId, string prompt, Action<string> onText, CancellationToken cancellationToken)
        {
            var payload = new JObject
            {
                ["model"] = model,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "user", ["content"] = prompt },
                },
                ["stream"] = true,
                ["max_tokens"] = 64,
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, ArniEndpoints.ApiOrigin + "/api/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Arni-Chat-Id", chatId);
            request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(errorBody, (int)response.StatusCode));
            }

            var reader = new SseReader();
            var text = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                foreach (var ev in reader.Push(buffer, read))
                {
                    var delta = ChatStreamParser.Parse(ev);
                    if (!string.IsNullOrEmpty(delta.Error))
                    {
                        throw new ApiCallException(200, delta.Error);
                    }

                    if (!string.IsNullOrEmpty(delta.Content))
                    {
                        text.Append(delta.Content);
                        onText(delta.Content);
                    }

                    if (delta.Done)
                    {
                        return text.ToString();
                    }
                }
            }

            if (reader.Remainder.Length > 0)
            {
                throw new ApiCallException(200, "The model stream ended before a completed event.");
            }

            return text.ToString();
        }

        public async Task<ModelTurn> CompleteAsync(string token, string model, string chatId, IList<ChatMessage> messages, bool withTools, Action<string> onDelta, CancellationToken cancellationToken)
        {
            var payload = new JObject
            {
                ["model"] = model,
                ["messages"] = ToMessages(messages),
                ["stream"] = true,
                ["max_tokens"] = 4096,
            };
            if (withTools)
            {
                payload["tools"] = ToolSchemas.AgentTools();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, ArniEndpoints.ApiOrigin + "/api/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Arni-Chat-Id", chatId);
            request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(errorBody, (int)response.StatusCode));
            }

            var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (media.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0
                && media.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) < 0)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseFull(body, onDelta);
            }

            var reader = new SseReader();
            var assembler = new ToolCallAssembler();
            var text = new StringBuilder();
            var finish = string.Empty;
            var done = false;
            var sawFinish = false;
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var buffer = new byte[4096];
                while (!done)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    foreach (var ev in reader.Push(buffer, read))
                    {
                        var delta = ChatStreamParser.Parse(ev);
                        if (delta.Malformed)
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(delta.Error))
                        {
                            return new ModelTurn { Error = delta.Error, Done = true, FinishReason = "error" };
                        }

                        if (!string.IsNullOrEmpty(delta.Content))
                        {
                            text.Append(delta.Content);
                            onDelta?.Invoke(delta.Content);
                        }

                        foreach (var call in delta.ToolCalls)
                        {
                            assembler.Add(call.Index, call.Id, call.Name, call.Arguments);
                        }

                        if (!string.IsNullOrEmpty(delta.FinishReason))
                        {
                            finish = delta.FinishReason;
                            sawFinish = true;
                        }

                        if (delta.Done)
                        {
                            done = true;
                            break;
                        }
                    }
                }
            }

            var turn = new ModelTurn
            {
                Content = text.ToString(),
                FinishReason = finish,
                Done = done || sawFinish,
                Interrupted = !done && !sawFinish,
            };
            if (assembler.TryComplete(out var calls, out var assembleError))
            {
                turn.ToolCalls = calls;
            }
            else if (calls.Count == 0 && !string.IsNullOrEmpty(assembleError) && sawFinish && finish == "tool_calls")
            {
                turn.Error = assembleError;
                turn.Interrupted = true;
            }
            else if (!string.IsNullOrEmpty(assembleError) && sawFinish && finish == "tool_calls")
            {
                turn.Error = assembleError;
                turn.ToolCalls = new List<ToolCallRecord>();
            }

            if (reader.Remainder.Length > 0 && !turn.Done)
            {
                turn.Interrupted = true;
                turn.Done = false;
            }

            return turn;
        }

        public async Task<IList<CatalogModel>> ListModelsAsync(string token, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ArniEndpoints.ApiOrigin + "/api/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(body, (int)response.StatusCode));
            }

            var models = new List<CatalogModel>();
            var data = JObject.Parse(body)["data"] as JArray;
            if (data == null)
            {
                return models;
            }

            foreach (var item in data)
            {
                var id = (string)item["id"] ?? string.Empty;
                if (id.Length == 0 || !CatalogModel.IsAvailableForArni(id))
                {
                    continue;
                }

                var name = (string)item["name"];
                var prompt = (string)item["pricing"]?["prompt"];
                var completion = (string)item["pricing"]?["completion"];
                var markedFree = item["arni_free"] != null && item["arni_free"].Type == JTokenType.Boolean && (bool)item["arni_free"];
                var model = new CatalogModel
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    Provider = CatalogModel.ProviderLabel((string)item["arni_provider"] ?? string.Empty),
                    PromptPerToken = prompt,
                    CompletionPerToken = completion,
                    Free = markedFree || (prompt != null && completion != null && decimal.TryParse(prompt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var promptValue) && promptValue == 0m && decimal.TryParse(completion, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var completionValue) && completionValue == 0m),
                };
                models.Add(model);
            }

            models.Sort((left, right) =>
            {
                var byName = string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
                return byName != 0 ? byName : string.Compare(left.Provider, right.Provider, StringComparison.CurrentCultureIgnoreCase);
            });
            return models;
        }

        public async Task<IList<string>> ModelIdsAsync(string token, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ArniEndpoints.ApiOrigin + "/api/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(body, (int)response.StatusCode));
            }

            var ids = new List<string>();
            var data = JObject.Parse(body)["data"] as JArray;
            if (data == null)
            {
                return ids;
            }

            foreach (var item in data)
            {
                var id = (string?)item["id"];
                if (!string.IsNullOrEmpty(id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        public async Task<AccountInfo> AccountAsync(string token, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ArniEndpoints.ApiOrigin + "/api/account");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(body, (int)response.StatusCode));
            }

            return AccountInfo.Parse(body);
        }

        private static ModelTurn ParseFull(string body, Action<string> onDelta)
        {
            var delta = ChatStreamParser.Parse(new SseEvent("message", body));
            if (!string.IsNullOrEmpty(delta.Error))
            {
                return new ModelTurn { Error = delta.Error, Done = true, FinishReason = "error" };
            }

            if (!string.IsNullOrEmpty(delta.Content))
            {
                onDelta?.Invoke(delta.Content);
            }

            var assembler = new ToolCallAssembler();
            foreach (var call in delta.ToolCalls)
            {
                assembler.Add(call.Index, call.Id, call.Name, call.Arguments);
            }

            var turn = new ModelTurn
            {
                Content = delta.Content,
                FinishReason = string.IsNullOrEmpty(delta.FinishReason) ? "stop" : delta.FinishReason,
                Done = true,
            };
            if (!assembler.TryComplete(out var calls, out var error))
            {
                turn.Error = error;
            }
            else
            {
                turn.ToolCalls = calls;
            }

            return turn;
        }

        private static JArray ToMessages(IList<ChatMessage> messages)
        {
            var array = new JArray();
            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                var item = new JObject
                {
                    ["role"] = message.Role,
                    ["content"] = message.Content ?? string.Empty,
                };
                if (!string.IsNullOrEmpty(message.ToolCallId))
                {
                    item["tool_call_id"] = message.ToolCallId;
                }

                if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                {
                    var calls = new JArray();
                    for (var callIndex = 0; callIndex < message.ToolCalls.Count; callIndex++)
                    {
                        var call = message.ToolCalls[callIndex];
                        calls.Add(new JObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = call.ArgumentsJson,
                            },
                        });
                    }

                    item["tool_calls"] = calls;
                }

                array.Add(item);
            }

            return array;
        }

        public async Task<string> FirstModelAsync(string token, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ArniEndpoints.ApiOrigin + "/api/models");
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(body, (int)response.StatusCode));
            }

            var id = (string?)JObject.Parse(body)["data"]?.First?["id"];
            if (id == null || id.Length == 0)
            {
                throw new ApiCallException((int)response.StatusCode, "No models are available for this account.");
            }

            return id;
        }
    }

}
