using System;
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
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(body));
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
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(body));
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
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(errorBody));
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
                throw new ApiCallException((int)response.StatusCode, ApiErrorText.FromBody(body));
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
