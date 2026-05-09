// Copyright (c) 2026 SquareZero Inc. - Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// OpenRouter provider using the Anthropic-compatible Messages endpoint.
    ///
    /// BIBIM's internal provider contract is already Anthropic-shaped, so this
    /// adapter can preserve the existing tool loop with very little translation.
    /// </summary>
    public class OpenRouterProvider : ILlmProvider
    {
        private const string Endpoint = "https://openrouter.ai/api/v1/messages";

        private readonly string _apiKey;
        private readonly string _modelId;
        private readonly HttpClient _httpClient;

        public string ProviderName => "openrouter";
        public string ModelId => _modelId;

        public OpenRouterProvider(string apiKey, string modelId, HttpClient httpClient)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("OpenRouter API key is required.", nameof(apiKey));
            if (string.IsNullOrEmpty(modelId))
                throw new ArgumentException("Model id is required.", nameof(modelId));

            _apiKey = apiKey;
            _modelId = modelId;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<JObject> SendNonStreamingAsync(
            JArray messages,
            string systemPrompt,
            JArray tools,
            CancellationToken ct,
            int maxTokens,
            bool jsonMode = false)
        {
            // jsonMode is intentionally prompt-driven here. OpenRouter exposes
            // structured output knobs, but support varies by routed model; the
            // planner already carries strict JSON-only instructions.
            var requestBody = new JObject
            {
                ["model"] = _modelId,
                ["max_tokens"] = maxTokens,
                ["system"] = systemPrompt ?? string.Empty,
                ["messages"] = SanitizeMessages(messages)
            };

            if (tools != null && tools.Count > 0)
                requestBody["tools"] = SanitizeTools(tools);

            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                request.Content = new StringContent(
                    requestBody.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");
                AddOpenRouterHeaders(request);

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"OpenRouter API {(int)response.StatusCode}: {body}");

                    return JObject.Parse(body);
                }
            }
        }

        public async Task<StreamResult> SendStreamingAsync(
            JArray messages,
            string systemPrompt,
            Action<string> onTextDelta,
            CancellationToken ct,
            int maxTokens)
        {
            var requestBody = new JObject
            {
                ["model"] = _modelId,
                ["max_tokens"] = maxTokens,
                ["system"] = systemPrompt ?? string.Empty,
                ["stream"] = true,
                ["messages"] = SanitizeMessages(messages)
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                request.Content = new StringContent(
                    requestBody.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");
                AddOpenRouterHeaders(request);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                using (var httpResponse = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct))
                {
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        string error = await httpResponse.Content.ReadAsStringAsync();
                        throw new HttpRequestException(
                            $"OpenRouter API {(int)httpResponse.StatusCode}: {error}");
                    }

                    var fullText = new StringBuilder();
                    int inputTokens = 0;
                    int outputTokens = 0;
                    int cachedTokens = 0;
                    int cacheCreationTokens = 0;

                    using (var stream = await httpResponse.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        string currentEvent = null;
                        var currentData = new StringBuilder();
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (line.Length == 0)
                            {
                                ProcessSseEvent(
                                    currentEvent, currentData.ToString(),
                                    fullText, onTextDelta,
                                    ref inputTokens, ref outputTokens, ref cachedTokens, ref cacheCreationTokens);
                                currentEvent = null;
                                currentData.Clear();
                                continue;
                            }

                            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                            {
                                currentEvent = line.Substring("event:".Length).Trim();
                                continue;
                            }

                            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                if (currentData.Length > 0) currentData.AppendLine();
                                currentData.Append(line.Substring("data:".Length).TrimStart());
                            }
                        }

                        ProcessSseEvent(
                            currentEvent, currentData.ToString(),
                            fullText, onTextDelta,
                            ref inputTokens, ref outputTokens, ref cachedTokens, ref cacheCreationTokens);
                    }

                    return new StreamResult
                    {
                        FullText = fullText.ToString(),
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        CachedInputTokens = cachedTokens,
                        CacheCreationInputTokens = cacheCreationTokens
                    };
                }
            }
        }

        private void AddOpenRouterHeaders(HttpRequestMessage request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            string referer = Environment.GetEnvironmentVariable("OPENROUTER_HTTP_REFERER");
            if (!string.IsNullOrWhiteSpace(referer))
                request.Headers.TryAddWithoutValidation("HTTP-Referer", referer);

            string title = Environment.GetEnvironmentVariable("OPENROUTER_APP_TITLE");
            if (string.IsNullOrWhiteSpace(title)) title = "BIBIM Revit";
            request.Headers.TryAddWithoutValidation("X-OpenRouter-Title", title);
        }

        private static JArray SanitizeMessages(JArray messages)
        {
            var copy = messages != null ? (JArray)messages.DeepClone() : new JArray();
            foreach (JToken msgToken in copy)
            {
                if (!(msgToken is JObject msg)) continue;
                if (!(msg["content"] is JArray contentArr)) continue;

                foreach (JToken blockToken in contentArr)
                {
                    if (!(blockToken is JObject block)) continue;
                    if (block["type"]?.ToString() == "tool_result")
                        block.Remove("name");
                }
            }
            return copy;
        }

        private static JArray SanitizeTools(JArray tools)
        {
            return tools != null ? (JArray)tools.DeepClone() : new JArray();
        }

        private static void ProcessSseEvent(
            string eventName,
            string data,
            StringBuilder fullText,
            Action<string> onTextDelta,
            ref int inputTokens,
            ref int outputTokens,
            ref int cachedTokens,
            ref int cacheCreationTokens)
        {
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") return;

            try
            {
                var payload = JObject.Parse(data);
                if (payload["error"] != null)
                {
                    Logger.Log("OpenRouterProvider", $"SSE error: {payload["error"]}");
                    return;
                }

                string type = payload["type"]?.ToString();
                string effectiveType = !string.IsNullOrWhiteSpace(type) ? type : eventName;

                switch (effectiveType)
                {
                    case "content_block_delta":
                        string deltaType = payload["delta"]?["type"]?.ToString();
                        if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase))
                        {
                            string deltaText = payload["delta"]?["text"]?.ToString();
                            if (!string.IsNullOrEmpty(deltaText))
                            {
                                fullText.Append(deltaText);
                                onTextDelta?.Invoke(deltaText);
                            }
                        }
                        break;

                    case "message_start":
                        var startUsage = payload["message"]?["usage"];
                        inputTokens = startUsage?["input_tokens"]?.Value<int>() ?? inputTokens;
                        outputTokens = startUsage?["output_tokens"]?.Value<int>() ?? outputTokens;
                        cachedTokens = startUsage?["cache_read_input_tokens"]?.Value<int>() ?? cachedTokens;
                        cacheCreationTokens = startUsage?["cache_creation_input_tokens"]?.Value<int>() ?? cacheCreationTokens;
                        break;

                    case "message_delta":
                        outputTokens = payload["usage"]?["output_tokens"]?.Value<int>() ?? outputTokens;
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("OpenRouterProvider", $"SSE parse skipped: {ex.Message}");
            }
        }
    }
}
