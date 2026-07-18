using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AzureOpenAI.Vsix;

internal static class AzureOpenAIChatClient
{
    private static readonly HttpClient HttpClient = new HttpClient();

    public static async Task<string> AskAsync(AzureOpenAIConfig config, string prompt, CancellationToken cancellationToken)
    {
        var preferResponsesApi = string.Equals(config.WireApi, "responses", StringComparison.OrdinalIgnoreCase);

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

            if (preferResponsesApi)
            {
                return await AskByResponsesApiAsync(config, prompt, cts.Token).ConfigureAwait(false);
            }

            try
            {
                return await AskByChatCompletionsApiAsync(config, prompt, cts.Token).ConfigureAwait(false);
            }
            catch (ApiCallException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && ex.ResponseBody.IndexOf("unsupported", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 某些部署（例如部分新模型）不支持 chat/completions，自动回退到 responses API。
                return await AskByResponsesApiAsync(config, prompt, cts.Token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> AskByChatCompletionsApiAsync(AzureOpenAIConfig config, string prompt, CancellationToken cancellationToken)
    {
        var apiBase = NormalizeToChatCompletionsBase(config.Endpoint);

        var requestUrl = string.Format(
            "{0}/deployments/{1}/chat/completions?api-version={2}",
            apiBase,
            Uri.EscapeDataString(config.Deployment),
            Uri.EscapeDataString(config.ApiVersion));

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(config.SystemPrompt))
        {
            messages.Add(new ChatMessage { Role = "system", Content = config.SystemPrompt ?? string.Empty });
        }

        messages.Add(new ChatMessage { Role = "user", Content = prompt });

        var payload = new ChatCompletionRequest
        {
            Messages = messages,
            Temperature = config.Temperature,
            MaxTokens = config.MaxTokens,
            Stream = false
        };

        var body = await PostJsonAsync(requestUrl, payload, config.ApiKey, cancellationToken).ConfigureAwait(false);

        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var first = choices[0];

                if (first.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString() ?? string.Empty;
                }
            }
        }

        throw new InvalidOperationException("Azure OpenAI(chat/completions) 返回格式无法识别: " + body);
    }

    private static async Task<string> AskByResponsesApiAsync(AzureOpenAIConfig config, string prompt, CancellationToken cancellationToken)
    {
        var apiBase = NormalizeToResponsesBase(config.Endpoint);
        var requestUrl = apiBase + "/responses";

        var payload = new ResponsesRequest
        {
            Model = config.Deployment,
            Input = prompt,
            Instructions = string.IsNullOrWhiteSpace(config.SystemPrompt) ? null : config.SystemPrompt,
            Temperature = config.Temperature,
            MaxOutputTokens = config.MaxTokens
        };

        var body = await PostJsonAsync(requestUrl, payload, config.ApiKey, cancellationToken).ConfigureAwait(false);

        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("output_text", out var outputText))
            {
                if (outputText.ValueKind == JsonValueKind.String)
                {
                    return outputText.GetString() ?? string.Empty;
                }

                if (outputText.ValueKind == JsonValueKind.Array)
                {
                    var sb0 = new StringBuilder();
                    foreach (var item in outputText.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            sb0.Append(item.GetString());
                        }
                    }

                    if (sb0.Length > 0)
                    {
                        return sb0.ToString();
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("output", out var output)
                && output.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();

                foreach (var outputItem in output.EnumerateArray())
                {
                    if (!outputItem.TryGetProperty("content", out var contentArray)
                        || contentArray.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var content in contentArray.EnumerateArray())
                    {
                        if (content.TryGetProperty("text", out var text)
                            && text.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(text.GetString());
                        }
                    }
                }

                if (sb.Length > 0)
                {
                    return sb.ToString();
                }
            }
        }

        throw new InvalidOperationException("Azure OpenAI(responses) 返回格式无法识别: " + body);
    }

    private static async Task<string> PostJsonAsync(string requestUrl, object payload, string apiKey, CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);

        using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
        {
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("api-key", apiKey);

            using (var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new ApiCallException(response.StatusCode, body, string.Format("Azure OpenAI 调用失败，HTTP {0}: {1}", (int)response.StatusCode, body));
                }

                return body;
            }
        }
    }

    private static string NormalizeToChatCompletionsBase(string endpoint)
    {
        var normalized = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Azure Endpoint 为空。请检查 config.toml。");
        }

        var deploymentsMarker = "/openai/deployments/";
        var markerIndex = normalized.IndexOf(deploymentsMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            return normalized.Substring(0, markerIndex + "/openai".Length);
        }

        if (normalized.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Substring(0, normalized.Length - 3);
        }

        if (normalized.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return normalized + "/openai";
        }

        return normalized;
    }

    private static string NormalizeToResponsesBase(string endpoint)
    {
        var normalized = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Azure Endpoint 为空。请检查 config.toml。");
        }

        var v1Marker = "/openai/v1/";
        var v1Index = normalized.IndexOf(v1Marker, StringComparison.OrdinalIgnoreCase);
        if (v1Index >= 0)
        {
            return normalized.Substring(0, v1Index + "/openai/v1".Length);
        }

        if (normalized.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var deploymentsMarker = "/openai/deployments/";
        var deploymentsIndex = normalized.IndexOf(deploymentsMarker, StringComparison.OrdinalIgnoreCase);
        if (deploymentsIndex >= 0)
        {
            return normalized.Substring(0, deploymentsIndex + "/openai/v1".Length);
        }

        if (normalized.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            return normalized + "/v1";
        }

        if (normalized.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return normalized + "/openai/v1";
        }

        return normalized;
    }

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.2;

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class ResponsesRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("input")]
        public string Input { get; set; } = string.Empty;

        [JsonPropertyName("instructions")]
        public string? Instructions { get; set; }

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.2;

        [JsonPropertyName("max_output_tokens")]
        public int? MaxOutputTokens { get; set; }
    }

    private sealed class ApiCallException : Exception
    {
        public ApiCallException(HttpStatusCode statusCode, string responseBody, string message) : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? string.Empty;
        }

        public HttpStatusCode StatusCode { get; }
        public string ResponseBody { get; }
    }
}
