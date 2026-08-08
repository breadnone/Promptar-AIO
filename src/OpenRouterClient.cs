using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAiGen;

public sealed class OpenRouterModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("pricing")]
    public OpenRouterPricing? Pricing { get; init; }

    [JsonPropertyName("context_length")]
    public int ContextLength { get; init; }

    public bool IsFree => Pricing is { Prompt: "0", Completion: "0" };
}

public sealed class OpenRouterPricing
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";

    [JsonPropertyName("completion")]
    public string Completion { get; init; } = "";
}

public sealed class OpenRouterModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenRouterModelInfo>? Data { get; init; }
}

public sealed class OpenRouterErrorResponse
{
    [JsonPropertyName("error")]
    public OpenRouterErrorDetail? Error { get; init; }
}

public sealed class OpenRouterErrorDetail
{
    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("code")]
    public int Code { get; init; }
}

public sealed class OpenRouterClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private List<OpenRouterModelInfo>? _cachedModels;
    private DateTime _modelsCacheTime;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _modelsCacheLock = new(1, 1);

    public OpenRouterClient(string? apiKey = null, string? baseUrl = null, int timeoutSeconds = 300)
    {
        _apiKey = apiKey;
        var url = baseUrl ?? "https://openrouter.ai/api/v1/";
        if (!url.EndsWith("/")) url += "/";
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(url),
            Timeout = timeoutSeconds <= 0
                ? System.Threading.Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(timeoutSeconds)
        };
        _http.DefaultRequestHeaders.ConnectionClose = false;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "https://github.com/MyAiGen");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "MyAiGen");
        }
        if (!string.IsNullOrWhiteSpace(_apiKey))
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public string GetBaseUrl() => _http.BaseAddress?.AbsoluteUri ?? "(null)";

    public async Task<List<OpenRouterModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        // Free model listing only works with OpenRouter's API
        if (_http.BaseAddress?.Host != "openrouter.ai")
            return new List<OpenRouterModelInfo>();

        if (_cachedModels != null && DateTime.UtcNow - _modelsCacheTime < CacheDuration)
            return _cachedModels;

        await _modelsCacheLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have refreshed the cache while we waited.
            if (_cachedModels != null && DateTime.UtcNow - _modelsCacheTime < CacheDuration)
                return _cachedModels;

            var modelsUrl = _http.BaseAddress != null
                ? new Uri(_http.BaseAddress, "models").AbsoluteUri
                : "https://openrouter.ai/api/v1/models";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var resp = await _http.GetAsync(modelsUrl, cts.Token);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"OpenRouter models API returned {(int)resp.StatusCode}");
            var result = await resp.Content.ReadFromJsonAsync<OpenRouterModelsResponse>(JsonWeb, cts.Token);
            _cachedModels = result?.Data?.ToList() ?? new List<OpenRouterModelInfo>();
            _modelsCacheTime = DateTime.UtcNow;
            return _cachedModels;
        }
        catch (HttpRequestException) { throw; }
        catch (Exception ex)
        {
            throw new HttpRequestException($"Failed to fetch OpenRouter models: {ex.Message}");
        }
        finally
        {
            _modelsCacheLock.Release();
        }
    }

    public async Task<ChatCompletionResponse> SendChatCompletionAsync(List<ChatMessage> messages,
        string? reasoningEffort = null, int maxTokens = 65536,
        float? temperature = null, float? topP = null, int? topK = null, float? repeatPenalty = null,
        List<ToolDefinition>? tools = null, string? toolChoice = null,
        string model = "google/gemma-2-9b-it:free",
        CancellationToken ct = default)
    {
        var req = new
        {
            model,

            messages = messages.Select(m =>
            {
                var obj = new Dictionary<string, object?>
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content ?? ""
                };
                if (m.ToolCalls is { Count: > 0 })
                    obj["tool_calls"] = m.ToolCalls;
                if (!string.IsNullOrEmpty(m.ToolCallId))
                    obj["tool_call_id"] = m.ToolCallId;
                if (!string.IsNullOrEmpty(m.Name))
                    obj["name"] = m.Name;
                return obj;
            }).ToList(),

            max_tokens = maxTokens,
            temperature = temperature ?? 0.7,
            top_p = topP,
            stream = false,
            reasoning_effort = reasoningEffort,
            tools = tools?.Select(t => new
            {
                type = t.Type,
                function = new
                {
                    name = t.Function.Name,
                    description = t.Function.Description,
                    parameters = t.Function.Parameters
                }
            }).ToList(),
            tool_choice = toolChoice,
            // When tools are in play, OpenRouter's default routing still lets a
            // provider that doesn't properly support function calling receive the
            // request - it just silently ignores `tools` and the underlying model
            // falls back to whatever ad-hoc text convention it was trained on
            // (that's the source of the <tool_call>/XML/bare-JSON dialect zoo).
            // require_parameters:true excludes those providers from routing
            // entirely, so this only fires when we're actually asking for tools.
            provider = tools is { Count: > 0 } ? new { require_parameters = true } : null
        };

        var json = JsonSerializer.Serialize(req, JsonWeb);
        var chatUrl = _http.BaseAddress != null
            ? new Uri(_http.BaseAddress, "chat/completions").AbsoluteUri
            : "https://openrouter.ai/api/v1/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, chatUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(request, ct);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            var errMsg = ParseOpenRouterError(body) ?? body.Truncate(300);
            throw new HttpRequestException($"OpenRouter API returned {(int)resp.StatusCode}: {errMsg}");
        }

        var result = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonWeb);
        if (result == null)
            throw new InvalidOperationException("OpenRouter returned null response");
        return result;
    }

    private string GetChatUrl()
    {
        return _http.BaseAddress != null
            ? new Uri(_http.BaseAddress, "chat/completions").AbsoluteUri
            : "https://openrouter.ai/api/v1/chat/completions";
    }

    public async Task<string> SendChatStreamAsync(List<ChatMessage> messages,
        KoboldCppClient.StreamChunkHandler onChunk,
        string? reasoningEffort = null, int maxTokens = 65536,
        float? temperature = null, float? topP = null, int? topK = null, float? repeatPenalty = null,
        List<ToolDefinition>? tools = null, string? toolChoice = null,
        string model = "google/gemma-2-9b-it:free",
        CancellationToken ct = default)
    {
        var req = new
        {
            model,

            messages = messages.Select(m =>
            {
                var obj = new Dictionary<string, object?>
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content ?? ""
                };
                if (m.ToolCalls is { Count: > 0 })
                    obj["tool_calls"] = m.ToolCalls;
                if (!string.IsNullOrEmpty(m.ToolCallId))
                    obj["tool_call_id"] = m.ToolCallId;
                if (!string.IsNullOrEmpty(m.Name))
                    obj["name"] = m.Name;
                return obj;
            }).ToList(),

            max_tokens = maxTokens,
            temperature = temperature ?? 0.7,
            top_p = topP,
            stream = true,
            reasoning_effort = reasoningEffort,
            tools = tools?.Select(t => new
            {
                type = t.Type,
                function = new
                {
                    name = t.Function.Name,
                    description = t.Function.Description,
                    parameters = t.Function.Parameters
                }
            }).ToList(),
            tool_choice = toolChoice,
            // Same reasoning as the non-streaming request: without this, a provider
            // that silently ignores `tools` can still be routed to, and the model
            // falls back to its own ad-hoc text convention instead of real tool_calls.
            provider = tools is { Count: > 0 } ? new { require_parameters = true } : null
        };

        var json = JsonSerializer.Serialize(req, JsonWeb);
        using var request = new HttpRequestMessage(HttpMethod.Post, GetChatUrl())
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            var errMsg = ParseOpenRouterError(errBody) ?? errBody.Truncate(300);
            throw new HttpRequestException($"OpenRouter API returned {(int)resp.StatusCode}: {errMsg}");
        }

        var fullContent = new StringBuilder();
        var fullReasoning = new StringBuilder();
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;

            try
            {
                var chunk = JsonSerializer.Deserialize<ChatCompletionResponse>(data, JsonWeb);
                if (chunk?.Choices == null || chunk.Choices.Count == 0) continue;
                var choice = chunk.Choices[0];
                if (choice.Delta == null) continue;
                if (choice.Delta.Content != null)
                {
                    fullContent.Append(choice.Delta.Content);
                    onChunk?.Invoke(choice.Delta.Content, null, false);
                }
                if (choice.Delta.ReasoningContent != null)
                {
                    fullReasoning.Append(choice.Delta.ReasoningContent);
                    onChunk?.Invoke(null, choice.Delta.ReasoningContent, false);
                }
                if (choice.FinishReason != null) break;
            }
            catch (JsonException) { continue; }
        }
        onChunk?.Invoke(null, null, true);

        var finalContent = fullContent.ToString();
        var reasoning = fullReasoning.ToString();
        if (!string.IsNullOrWhiteSpace(reasoning))
            finalContent = $"<reasoning>\n{reasoning}\n</reasoning>\n\n{finalContent}";
        return string.IsNullOrWhiteSpace(finalContent) ? "(empty — model returned EOS)" : finalContent;
    }

    private static string? ParseOpenRouterError(string body)
    {
        try
        {
            var errResp = JsonSerializer.Deserialize<OpenRouterErrorResponse>(body, JsonWeb);
            if (errResp?.Error != null && !string.IsNullOrWhiteSpace(errResp.Error.Message))
            {
                var msg = errResp.Error.Message;
                if (errResp.Error.Code == 429)
                    msg = "FREE QUOTA EXCEEDED: OpenRouter free tier rate limit hit. Wait a moment or try a different free model.";
                else if (errResp.Error.Code == 401 || msg.Contains("auth", StringComparison.OrdinalIgnoreCase))
                    msg = "OpenRouter authentication failed. Get a free API key at https://openrouter.ai/keys and enter it in Settings > Text > Advanced > OR API Key.";
                return msg;
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        _http?.Dispose();
        _modelsCacheLock.Dispose();
    }
}