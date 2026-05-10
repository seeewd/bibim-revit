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
    /// OpenRouter provider using the OpenAI-compatible Chat Completions endpoint.
    ///
    /// BIBIM's internal provider contract is Anthropic-shaped, so this adapter
    /// translates messages/tools to Chat Completions and translates responses
    /// back to the orchestrator's Anthropic-shaped response object.
    /// </summary>
    public class OpenRouterProvider : ILlmProvider
    {
        private const string ChatCompletionsEndpoint = "https://openrouter.ai/api/v1/chat/completions";

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
            // jsonMode stays prompt-driven for OpenRouter. Routed model support
            // for response_format varies and can reject otherwise valid requests.
            var requestBody = new JObject
            {
                ["model"] = _modelId,
                ["max_tokens"] = maxTokens,
                ["messages"] = TranslateMessagesToChat(messages, systemPrompt)
            };

            if (tools != null && tools.Count > 0)
                requestBody["tools"] = TranslateToolsToChat(tools);

            using (var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint))
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

                    return TranslateChatResponseToAnthropicShape(JObject.Parse(body));
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
                ["stream"] = true,
                ["stream_options"] = new JObject { ["include_usage"] = true },
                ["messages"] = TranslateMessagesToChat(messages, systemPrompt)
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint))
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

                    using (var stream = await httpResponse.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                            string data = line.Substring("data:".Length).Trim();
                            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") continue;
                            ProcessChatSseEvent(data, fullText, onTextDelta, ref inputTokens, ref outputTokens, ref cachedTokens);
                        }
                    }

                    return new StreamResult
                    {
                        FullText = fullText.ToString(),
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        CachedInputTokens = cachedTokens
                    };
                }
            }
        }

        private void AddOpenRouterHeaders(HttpRequestMessage request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            string referer = Environment.GetEnvironmentVariable("OPENROUTER_HTTP_REFERER");
            if (string.IsNullOrWhiteSpace(referer)) referer = "https://bibim.app";
            request.Headers.TryAddWithoutValidation("HTTP-Referer", referer);

            string title = Environment.GetEnvironmentVariable("OPENROUTER_APP_TITLE");
            if (string.IsNullOrWhiteSpace(title)) title = "BIBIM Revit Agent";
            request.Headers.TryAddWithoutValidation("X-Title", title);
            request.Headers.TryAddWithoutValidation("X-OpenRouter-Title", title);
        }

        private static JArray TranslateMessagesToChat(JArray anthropicMessages, string systemPrompt)
        {
            var chatMessages = new JArray();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                chatMessages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt
                });
            }

            if (anthropicMessages == null) return chatMessages;

            foreach (JObject msg in anthropicMessages)
            {
                string role = msg["role"]?.ToString();
                var content = msg["content"];
                if (string.IsNullOrWhiteSpace(role)) continue;

                if (content == null || content.Type == JTokenType.String)
                {
                    chatMessages.Add(new JObject
                    {
                        ["role"] = role,
                        ["content"] = content?.ToString() ?? string.Empty
                    });
                    continue;
                }

                if (!(content is JArray contentArr))
                {
                    chatMessages.Add(new JObject
                    {
                        ["role"] = role,
                        ["content"] = content.ToString(Formatting.None)
                    });
                    continue;
                }

                var text = new StringBuilder();
                var toolCalls = new JArray();

                foreach (JObject block in contentArr)
                {
                    string type = block["type"]?.ToString();
                    if (type == "text")
                    {
                        text.Append(block["text"]?.ToString() ?? string.Empty);
                    }
                    else if (type == "tool_use")
                    {
                        toolCalls.Add(new JObject
                        {
                            ["id"] = block["id"]?.ToString(),
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["name"] = block["name"]?.ToString(),
                                ["arguments"] = block["input"]?.ToString(Formatting.None) ?? "{}"
                            }
                        });
                    }
                    else if (type == "tool_result")
                    {
                        if (text.Length > 0)
                        {
                            chatMessages.Add(new JObject
                            {
                                ["role"] = "user",
                                ["content"] = text.ToString()
                            });
                            text.Clear();
                        }

                        chatMessages.Add(new JObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = block["tool_use_id"]?.ToString(),
                            ["content"] = block["content"]?.ToString() ?? string.Empty
                        });
                    }
                }

                if (toolCalls.Count > 0)
                {
                    var assistant = new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = text.Length > 0 ? text.ToString() : null,
                        ["tool_calls"] = toolCalls
                    };
                    chatMessages.Add(assistant);
                }
                else if (text.Length > 0)
                {
                    chatMessages.Add(new JObject
                    {
                        ["role"] = role,
                        ["content"] = text.ToString()
                    });
                }
            }

            return chatMessages;
        }

        private static JArray TranslateToolsToChat(JArray anthropicTools)
        {
            var chatTools = new JArray();
            if (anthropicTools == null) return chatTools;

            foreach (JObject tool in anthropicTools)
            {
                chatTools.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool["name"]?.ToString(),
                        ["description"] = tool["description"]?.ToString(),
                        ["parameters"] = tool["input_schema"] ?? new JObject()
                    }
                });
            }

            return chatTools;
        }

        private static JObject TranslateChatResponseToAnthropicShape(JObject chatResponse)
        {
            var content = new JArray();
            string stopReason = "end_turn";
            var choice = chatResponse["choices"]?[0] as JObject;
            var message = choice?["message"] as JObject;

            string text = ExtractChatMessageText(message?["content"]);
            if (!string.IsNullOrEmpty(text))
            {
                content.Add(new JObject
                {
                    ["type"] = "text",
                    ["text"] = text
                });
            }

            var toolCalls = message?["tool_calls"] as JArray;
            if (toolCalls != null && toolCalls.Count > 0)
            {
                foreach (JObject call in toolCalls)
                {
                    JObject argsObj;
                    try
                    {
                        argsObj = JObject.Parse(call["function"]?["arguments"]?.ToString() ?? "{}");
                    }
                    catch
                    {
                        argsObj = new JObject();
                    }

                    content.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call["id"]?.ToString(),
                        ["name"] = call["function"]?["name"]?.ToString(),
                        ["input"] = argsObj
                    });
                }

                stopReason = "tool_use";
            }

            string finishReason = choice?["finish_reason"]?.ToString();
            if (finishReason == "length") stopReason = "max_tokens";

            var rawUsage = chatResponse["usage"] ?? new JObject();
            var usage = new JObject
            {
                ["input_tokens"] = rawUsage["prompt_tokens"] ?? 0,
                ["output_tokens"] = rawUsage["completion_tokens"] ?? 0,
                ["cache_read_input_tokens"] = rawUsage["prompt_tokens_details"]?["cached_tokens"] ?? 0,
                ["cache_creation_input_tokens"] = 0
            };

            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = stopReason,
                ["usage"] = usage,
                ["model"] = chatResponse["model"] ?? string.Empty
            };
        }

        private static string ExtractChatMessageText(JToken content)
        {
            if (content == null || content.Type == JTokenType.Null) return string.Empty;
            if (content.Type == JTokenType.String) return content.ToString();

            if (content is JArray arr)
            {
                var sb = new StringBuilder();
                foreach (JObject part in arr)
                {
                    string type = part["type"]?.ToString();
                    if (type == "text")
                        sb.Append(part["text"]?.ToString() ?? string.Empty);
                }
                return sb.ToString();
            }

            return content.ToString();
        }

        private static void ProcessChatSseEvent(
            string data,
            StringBuilder fullText,
            Action<string> onTextDelta,
            ref int inputTokens,
            ref int outputTokens,
            ref int cachedTokens)
        {
            try
            {
                var payload = JObject.Parse(data);
                if (payload["error"] != null)
                {
                    Logger.Log("OpenRouterProvider", $"SSE error: {payload["error"]}");
                    return;
                }

                var choices = payload["choices"] as JArray;
                if (choices != null && choices.Count > 0)
                {
                    string deltaText = choices[0]?["delta"]?["content"]?.ToString();
                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        fullText.Append(deltaText);
                        onTextDelta?.Invoke(deltaText);
                    }
                }

                var usage = payload["usage"];
                if (usage != null)
                {
                    inputTokens = usage["prompt_tokens"]?.Value<int>() ?? inputTokens;
                    outputTokens = usage["completion_tokens"]?.Value<int>() ?? outputTokens;
                    cachedTokens = usage["prompt_tokens_details"]?["cached_tokens"]?.Value<int>() ?? cachedTokens;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("OpenRouterProvider", $"SSE parse skipped: {ex.Message}");
            }
        }
    }
}
