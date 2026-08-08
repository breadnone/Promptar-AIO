using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAiGen;

/// <summary>
/// Checks TCP reachability without ever throwing. HttpClient always throws
/// HttpRequestException on connection-refused, and that throw happens (and gets
/// logged by the debugger as a first-chance exception) the instant it occurs —
/// before any catch block runs, so wrapping HttpClient calls in try/catch cannot
/// prevent the "Exception thrown" console spam. A raw Socket connect via
/// SocketAsyncEventArgs reports failure through e.SocketError instead of throwing,
/// so we use it as a gate: only call HttpClient once the TCP handshake has actually
/// succeeded — the only way to avoid triggering the exception at all.
/// </summary>
internal static class TcpProbe
{
    public static Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>();
        var args = new SocketAsyncEventArgs { RemoteEndPoint = new DnsEndPoint(host, port) };
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var cleanedUp = 0;
        Timer? timer = null;

        void Cleanup()
        {
            // Guard against the timeout timer and the socket completion callback both
            // firing (racing each other right at the timeout boundary) and double-disposing.
            if (Interlocked.Exchange(ref cleanedUp, 1) != 0) return;
            args.Completed -= OnCompleted;
            args.Dispose();
            socket.Dispose();
            timer?.Dispose();
        }

        void OnCompleted(object? _, SocketAsyncEventArgs e)
        {
            var success = e.SocketError == SocketError.Success;
            Cleanup();
            tcs.TrySetResult(success);
        }

        args.Completed += OnCompleted;
        timer = new Timer(_ =>
        {
            Cleanup();
            tcs.TrySetResult(false);
        }, null, timeoutMs, Timeout.Infinite);

        if (!socket.ConnectAsync(args))
        {
            // Completed synchronously — Completed event is not raised in that case.
            var success = args.SocketError == SocketError.Success;
            Cleanup();
            tcs.TrySetResult(success);
        }

        return tcs.Task;
    }
}

public sealed class KoboldCppClient : IDisposable
{
    // Same file MainWindow.Log() writes to, so request/response diagnostics land
    // alongside the rest of the session log instead of only in the GUI's 100-line
    // box (which evicts old lines fast) or the VS Output window (buried in noise).
    private static readonly string _diagLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PromptWhizz", "app.log");
    private static readonly object _diagLogLock = new();

    private static void WriteDiagLog(string tag, string content)
    {
        try
        {
            lock (_diagLogLock)
            {
                var dir = Path.GetDirectoryName(_diagLogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_diagLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {content}\n");
            }
        }
        catch { }
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly int _port;
    private bool _disposed;
    private string? _workingEndpoint;
    private bool _hasSdcppVideo;
    private readonly SemaphoreSlim _endpointDiscoveryLock = new(1, 1);

    public bool HasVideoCapability => _hasSdcppVideo;

    public KoboldCppClient(int port)
    {
        _port = port;
        // SocketsHttpHandler's default connection pool will happily keep reusing a
        // keep-alive TCP connection for a long time. koboldcpp periodically does
        // blocking work (SmartCache KV state save/restore — visible in app.log as
        // "state_write_data: writing state") that can cause it to close a connection
        // out from under us between requests. HttpClient doesn't discover a pooled
        // connection is dead until it tries to write to it, and that failure surfaces
        // as "HttpRequestException: An error occurred while sending the request." —
        // which is exactly what was spamming the debugger during agent loops that fire
        // requests seconds apart. A short PooledConnectionLifetime forces the pool to
        // retire and reopen connections regularly instead of trusting a stale one.
        var handler = new SocketsHttpHandler
        {
            // PooledConnectionLifetime = Zero means every request opens a brand-new
            // connection instead of reusing one from the pool. This isn't just tuning —
            // it's the only way to guarantee we never send on a connection koboldcpp
            // closed on its end (e.g. while blocked on the SmartCache KV state
            // save/restore disk I/O visible in app.log as "state_write_data"). A retry
            // wrapper still lets the debugger log the first, doomed attempt's exception
            // before the catch runs; never reusing a connection means that first attempt
            // is never made on a stale socket in the first place. The reconnect cost is
            // negligible on localhost.
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://localhost:{port}/"),
            // No ceiling here — generation time depends entirely on hardware, context
            // size, and max_tokens, and there is no safe universal cap. Individual
            // per-call CancellationTokenSources below are used only where a call
            // genuinely should fail fast (probes, abort); the actual chat/vision/
            // multimodal generation calls pass the caller's token through uncapped.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<bool> IsReadyAsync(int timeoutMs = 5000)
    {
        // Gate on a raw TCP probe first. Calling HttpClient against a port nothing is
        // listening on always throws HttpRequestException, and that throw is logged by
        // the debugger the instant it happens — before this try/catch ever runs — so it
        // cannot be silenced from inside the catch. Only once the socket actually
        // connects do we know the HTTP call has a real chance of succeeding without
        // throwing on connection-refused.
        if (!await TcpProbe.IsPortOpenAsync("localhost", _port, Math.Min(timeoutMs, 3000)))
            return false;

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var resp = await _http.GetAsync("/api/v1/info/version", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ImageGenerationResult> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        if (_workingEndpoint == null)
        {
            await _endpointDiscoveryLock.WaitAsync(ct);
            try
            {
                // Re-check: another caller may have finished discovery while we waited.
                _workingEndpoint ??= await DiscoverEndpointAsync();
            }
            finally
            {
                _endpointDiscoveryLock.Release();
            }
        }

        if (request.InitImagesBase64 is { Count: > 0 })
            return await GenerateViaA1111Img2ImgAsync(request, ct);

        return _workingEndpoint switch
        {
            "openai" => await GenerateViaOpenAIAsync(request, ct),
            "a1111" => await GenerateViaA1111Async(request, ct),
            _ => await GenerateViaA1111Async(request, ct),
        };
    }

    private async Task<string> DiscoverEndpointAsync()
    {
        // Check for sdcpp native API (video gen capability)
        if (await ProbeGetAsync("/sdcpp/v1/capabilities", 3000))
        {
            try
            {
                var capResp = await _http.GetStringAsync("/sdcpp/v1/capabilities");
                var caps = JsonSerializer.Deserialize<SdcppCapabilities>(capResp, JsonWeb);
                _hasSdcppVideo = caps?.Capabilities?.Contains("vid_gen", StringComparer.OrdinalIgnoreCase) == true;
            }
            catch { }
        }

        // Fast GET probes first - these return quickly without triggering generation
        if (await ProbeGetAsync("/sdapi/v1/sd-models", 3000))
            return "a1111";
        if (await ProbeGetAsync("/sdapi/v1/samplers", 3000))
            return "a1111";

        // POST with minimal/empty body to check if route exists (KoboldCpp returns 400 for empty body on valid routes)
        if (await ProbePostExistsAsync("/sdapi/v1/txt2img", 3000))
            return "a1111";
        if (await ProbePostExistsAsync("/v1/images/generations", 3000))
            return "openai";

        // Last resort: assume A1111 (most likely to work with modern KoboldCpp)
        return "a1111";
    }

    private async Task<bool> ProbeGetAsync(string path, int timeoutMs)
    {
        // Same reasoning as IsReadyAsync: don't let HttpClient touch a dead port.
        if (!await TcpProbe.IsPortOpenAsync("localhost", _port, Math.Min(timeoutMs, 3000)))
            return false;

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var resp = await _http.GetAsync(path, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ProbePostExistsAsync(string path, int timeoutMs)
    {
        if (!await TcpProbe.IsPortOpenAsync("localhost", _port, Math.Min(timeoutMs, 3000)))
            return false;

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(path, content, cts.Token);
            // Route exists if we get any response other than 404
            return resp.StatusCode != System.Net.HttpStatusCode.NotFound;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ImageGenerationResult> GenerateViaOpenAIAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        var payload = new
        {
            prompt = request.Prompt,
            n = 1,
            size = $"{request.Width}x{request.Height}",
            response_format = "b64_json",
            model = "koboldcpp_current",
            seed = request.Seed >= 0 ? request.Seed : null as long?,
            steps = request.Steps,
            cfg_scale = request.CfgScale
        };

        var body = await PostAndGetStringAsync("/v1/images/generations", payload, "openai", ct);
        var result = JsonSerializer.Deserialize<OpenAIImageResponse>(body);
        if (result?.Data == null || result.Data.Count == 0 || string.IsNullOrEmpty(result.Data[0].B64Json))
            throw new InvalidOperationException("No image data: " + (body.Truncate(300) ?? "null"));

        return new ImageGenerationResult
        {
            ImageBase64 = result.Data[0].B64Json,
            Seed = request.Seed >= 0 ? request.Seed : 0,
            Prompt = request.Prompt
        };
    }

    private async Task<ImageGenerationResult> GenerateViaA1111Async(ImageGenerationRequest request, CancellationToken ct)
    {
        var payload = new
        {
            prompt = request.Prompt,
            negative_prompt = request.NegativePrompt ?? "",
            width = request.Width,
            height = request.Height,
            steps = request.Steps,
            cfg_scale = request.CfgScale,
            seed = request.Seed >= 0 ? request.Seed : -1,
            batch_size = 1,
            sampler_name = "euler"
        };

        var body = await PostAndGetStringAsync("/sdapi/v1/txt2img", payload, "A1111", ct);
        var result = JsonSerializer.Deserialize<A1111ImageResponse>(body);
        if (result?.Images == null || result.Images.Length == 0)
            throw new InvalidOperationException("No image data: " + (body.Truncate(300) ?? "null"));

        return new ImageGenerationResult
        {
            ImageBase64 = result.Images[0],
            Seed = result.Seed ?? (request.Seed >= 0 ? request.Seed : 0),
            Prompt = request.Prompt
        };
    }

    private async Task<ImageGenerationResult> GenerateViaA1111Img2ImgAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        var payload = new
        {
            init_images = request.InitImagesBase64,
            denoising_strength = request.DenoisingStrength,
            prompt = request.Prompt,
            negative_prompt = request.NegativePrompt ?? "",
            width = request.Width,
            height = request.Height,
            steps = request.Steps,
            cfg_scale = request.CfgScale,
            seed = request.Seed >= 0 ? request.Seed : -1,
            batch_size = 1,
            sampler_name = "euler"
        };

        var body = await PostAndGetStringAsync("/sdapi/v1/img2img", payload, "A1111-img2img", ct);
        var result = JsonSerializer.Deserialize<A1111ImageResponse>(body);
        if (result?.Images == null || result.Images.Length == 0)
            throw new InvalidOperationException("No image data: " + (body.Truncate(300) ?? "null"));

        return new ImageGenerationResult
        {
            ImageBase64 = result.Images[0],
            Seed = result.Seed ?? (request.Seed >= 0 ? request.Seed : 0),
            Prompt = request.Prompt
        };
    }

    public async Task<VideoGenerationResult> GenerateVideoAsync(VideoGenerationRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            prompt = request.Prompt,
            negative_prompt = request.NegativePrompt ?? "",
            steps = request.Steps,
            cfg_scale = request.CfgScale,
            width = request.Width,
            height = request.Height,
            seed = request.Seed >= 0 ? request.Seed : -1,
            video_frames = request.Frames,
            fps = request.Fps,
            output_format = request.OutputFormat,
            sampler = "euler"
        };

        // Submit the video generation job
        string jobId;
        using (var submitCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            submitCts.CancelAfter(TimeSpan.FromSeconds(30));
            var submitResp = await _http.PostAsJsonAsync("/sdcpp/v1/vid_gen", payload, submitCts.Token);
            if (submitResp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException("Video endpoint not found. This KoboldCpp version may not support video, or the model isn't video-capable.");
            submitResp.EnsureSuccessStatusCode();
            var submitBody = await submitResp.Content.ReadAsStringAsync();
            var jobResult = JsonSerializer.Deserialize<SdcppJobResponse>(submitBody, JsonWeb);
            jobId = jobResult?.Id ?? throw new InvalidOperationException("No job ID returned: " + submitBody.Truncate(200));
        }

        // Poll for completion
        var pollInterval = TimeSpan.FromSeconds(2);
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromMinutes(10);
        var pollUrl = $"/sdcpp/v1/jobs/{jobId}";

        while (!ct.IsCancellationRequested && DateTime.UtcNow - startTime < timeout)
        {
            await Task.Delay(pollInterval, ct);

            using var pollResp = await _http.GetAsync(pollUrl, ct);
            pollResp.EnsureSuccessStatusCode();
            var pollBody = await pollResp.Content.ReadAsStringAsync();
            var status = JsonSerializer.Deserialize<SdcppJobResponse>(pollBody, JsonWeb);

            if (status == null)
                throw new InvalidOperationException("Invalid job status response");

            switch (status.Status?.ToLowerInvariant())
            {
                case "completed":
                    if (status.Result?.B64Json == null)
                        throw new InvalidOperationException("Job completed but no video data");
                    return new VideoGenerationResult
                    {
                        VideoBase64 = status.Result.B64Json,
                        MimeType = status.Result.MimeType ?? "video/webm",
                        OutputFormat = status.Result.OutputFormat ?? request.OutputFormat,
                        Fps = status.Result.Fps > 0 ? status.Result.Fps : request.Fps,
                        FrameCount = status.Result.FrameCount,
                        Seed = request.Seed >= 0 ? request.Seed : 0,
                        Prompt = request.Prompt
                    };

                case "failed":
                    throw new InvalidOperationException($"Video generation failed: {status.Error ?? "Unknown error"}");

                case "cancelled":
                    throw new OperationCanceledException("Video generation was cancelled on server");

                    // "queued" or "running" — keep polling
            }

            // Gradually increase poll interval up to 5 seconds
            if (pollInterval < TimeSpan.FromSeconds(5))
                pollInterval = pollInterval.Add(TimeSpan.FromSeconds(0.5));
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException("Video generation did not complete within 10 minutes");
    }

    public async Task CancelVideoJobAsync(string jobId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _http.PostAsync($"/sdcpp/v1/jobs/{jobId}/cancel", null, cts.Token);
        }
        catch { }
    }

    public async Task<string> SendVisionChatAsync(string imagePath, string prompt, int maxTokens = 65536, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image file not found", imagePath);
        var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
        var ext = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
        var mime = ext switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            _ => "image/png"
        };
        var b64 = Convert.ToBase64String(imageBytes);
        var dataUri = $"data:{mime};base64,{b64}";

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("max_tokens", maxTokens);
            writer.WriteNumber("temperature", 0.7);
            writer.WriteBoolean("stream", false);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "image_url");
            writer.WriteStartObject("image_url");
            writer.WriteString("url", dataUri);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(ms.ToArray());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // No timeout — generation time is unbounded (depends on hardware/context/max_tokens); only the caller's own CancellationToken can stop this.
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("/v1/chat/completions", content, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Server returned {(int)resp.StatusCode}: {errBody.Truncate(500)}");
        }
        var body = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonWeb);
        var msg = result?.Choices?.FirstOrDefault()?.Message;
        var contentText = msg?.Content;
        return string.IsNullOrWhiteSpace(contentText) ? $"(empty — model returned EOS)" : contentText;
    }

    // null = unknown (not yet probed), true = koboldcpp's /api/extra/websearch works,
    // false = it doesn't (404 / unsupported build) — determined once, not re-thrown every call.
    private bool? _koboldWebSearchSupported;

    public async Task<List<WebSearchResult>> WebSearchAsync(string query, CancellationToken ct = default)
    {
        if (_koboldWebSearchSupported != false)
        {
            var viaKobold = await WebSearchViaKoboldCppAsync(query, ct);
            if (viaKobold != null)
            {
                _koboldWebSearchSupported = true;
                return viaKobold;
            }
            // viaKobold == null means the koboldcpp route is unsupported/unavailable for
            // this session — remember that so subsequent calls skip straight to the direct
            // fallback instead of hitting the same failure (and, previously, throwing the
            // same HttpRequestException) on every single agent-driven search.
            _koboldWebSearchSupported = false;
        }

        return await WebSearchDirectAsync(query, ct);
    }

    /// Returns null (never throws) when koboldcpp's websearch route is unsupported,
    /// unreachable, or errors — callers treat null as "fall back to direct search".
    /// Exceptions are reserved for genuinely unexpected failures during JSON handling,
    /// not for the routine "this build doesn't have the route" case.
    private async Task<List<WebSearchResult>?> WebSearchViaKoboldCppAsync(string query, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var payload = new { q = query };
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync("/api/extra/websearch", payload, cts.Token);
        }
        catch
        {
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            // Covers the 404 "route not supported by this koboldcpp build" case as well
            // as any other server-side failure — both mean "use the direct fallback",
            // neither is exceptional enough to warrant throwing.
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            var results = JsonSerializer.Deserialize<List<WebSearchResult>>(body, JsonWeb);
            return results ?? new List<WebSearchResult>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<WebSearchResult>> WebSearchDirectAsync(string query, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
        var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        var results = new List<WebSearchResult>();
        // Parse DuckDuckGo HTML results — extract result divs
        int idx = 0;
        while (true)
        {
            var linkStart = html.IndexOf(@"<a rel=""nofollow"" class=""result__a"" href=""", idx, StringComparison.Ordinal);
            if (linkStart < 0) break;
            linkStart += @"<a rel=""nofollow"" class=""result__a"" href=""".Length;
            var linkEnd = html.IndexOf('"', linkStart);
            if (linkEnd < 0) break;
            var urlStr = html[linkStart..linkEnd];
            // unescape HTML entities in URL
            urlStr = System.Net.WebUtility.HtmlDecode(urlStr);

            // Title is inside <a ...>...</a>
            var titleStart = html.IndexOf('>', linkEnd) + 1;
            var titleEnd = html.IndexOf("</a>", titleStart, StringComparison.Ordinal);
            var title = titleStart > 0 && titleEnd > titleStart
                ? System.Net.WebUtility.HtmlDecode(html[titleStart..titleEnd].Trim())
                : "";

            // Snippet is in the next <a class="result__snippet" ...>
            var snippetTag = @"<a class=""result__snippet""";
            var snipStart = html.IndexOf(snippetTag, titleEnd, StringComparison.Ordinal);
            if (snipStart < 0) { idx = titleEnd + 1; continue; }
            snipStart = html.IndexOf('>', snipStart) + 1;
            var snipEnd = html.IndexOf("</a>", snipStart, StringComparison.Ordinal);
            var snippet = snipStart > 0 && snipEnd > snipStart
                ? System.Net.WebUtility.HtmlDecode(html[snipStart..snipEnd].Trim())
                : "";

            results.Add(new WebSearchResult { Title = title, Url = urlStr, Description = snippet, Content = snippet });
            idx = snipEnd + 1;

            if (results.Count >= 10) break;
        }

        return results;
    }

    public async Task<string> SendChatAsync(List<ChatMessage> messages,
        string? reasoningEffort = null, int maxTokens = 65536,
        float? temperature = null, float? topP = null, int? topK = null, float? repeatPenalty = null,
        CancellationToken ct = default)
    {
        var req = new ChatCompletionRequest
        {
            Messages = messages,
            MaxTokens = maxTokens,
            Temperature = temperature ?? 0.7f,
            Stream = false,
            ReasoningEffort = reasoningEffort,
            TopP = topP,
            TopK = topK,
            RepeatPenalty = repeatPenalty
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // No timeout — generation time is unbounded (depends on hardware/context/max_tokens); only the caller's own CancellationToken can stop this.
        var resp = await _http.PostAsJsonAsync("/v1/chat/completions", req, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Server returned {(int)resp.StatusCode}: {err.Truncate(500)}");
        }
        var body = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonWeb);
        var msg = result?.Choices?.FirstOrDefault()?.Message;
        var content = msg?.Content;

        // Include reasoning content in the response
        var reasoning = msg?.ReasoningContent;
        var finalContent = content ?? "";
        if (!string.IsNullOrWhiteSpace(reasoning))
            finalContent = $"<reasoning>\n{reasoning}\n</reasoning>\n\n{finalContent}";

        return string.IsNullOrWhiteSpace(finalContent) ? $"(empty — model returned EOS)" : finalContent;
    }

    public delegate void StreamChunkHandler(string? content, string? reasoning, bool isDone);

    public async Task<string> SendChatStreamAsync(List<ChatMessage> messages, StreamChunkHandler onChunk,
        string? reasoningEffort = null, int maxTokens = 65536,
        float? temperature = null, float? topP = null, int? topK = null, float? repeatPenalty = null,
        List<ToolDefinition>? tools = null, string? toolChoice = null,
        CancellationToken ct = default)
    {
        var req = new ChatCompletionRequest
        {
            Messages = messages,
            MaxTokens = maxTokens,
            Temperature = temperature ?? 0.7f,
            Stream = true,
            ReasoningEffort = reasoningEffort,
            TopP = topP,
            TopK = topK,
            RepeatPenalty = repeatPenalty,
            Tools = tools,
            ToolChoice = toolChoice
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // No timeout — generation time is unbounded (depends on hardware/context/max_tokens); only the caller's own CancellationToken can stop this.
        var json = JsonSerializer.Serialize(req, JsonWeb);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        // ResponseHeadersRead is essential here: without it, HttpClient buffers the
        // entire response body before the call returns, defeating streaming — the
        // caller would only ever see the whole reply at once instead of chunk by chunk.
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Server returned {(int)resp.StatusCode}: {err.Truncate(500)}");
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
        onChunk?.Invoke(null, null, true);
        var finalContent = fullContent.ToString();
        var reasoning = fullReasoning.ToString();
        if (!string.IsNullOrWhiteSpace(reasoning))
            finalContent = $"<reasoning>\n{reasoning}\n</reasoning>\n\n{finalContent}";
        return string.IsNullOrWhiteSpace(finalContent) ? $"(empty — model returned EOS)" : finalContent;
    }

    /// Sends a non-streaming chat completion and returns the full response including tool_calls.
    public async Task<ChatCompletionResponse> SendChatCompletionAsync(List<ChatMessage> messages,
        string? reasoningEffort = null, int maxTokens = 65536,
        float? temperature = null, float? topP = null, int? topK = null, float? repeatPenalty = null,
        List<ToolDefinition>? tools = null, string? toolChoice = null,
        CancellationToken ct = default)
    {
        var req = new ChatCompletionRequest
        {
            Messages = messages,
            MaxTokens = maxTokens,
            Temperature = temperature ?? 0.7f,
            Stream = false,
            ReasoningEffort = reasoningEffort,
            TopP = topP,
            TopK = topK,
            RepeatPenalty = repeatPenalty,
            Tools = tools,
            ToolChoice = toolChoice
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // No timeout — generation time is unbounded (depends on hardware/context/max_tokens); only the caller's own CancellationToken can stop this.
        var json = JsonSerializer.Serialize(req, JsonWeb);
        if (tools is { Count: > 0 })
            WriteDiagLog("Kobold REQUEST", json);

        HttpResponseMessage resp;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            resp = await _http.SendAsync(request, cts.Token);
        }
        catch (HttpRequestException) when (!cts.IsCancellationRequested)
        {
            // A pooled keep-alive connection that koboldcpp closed server-side between
            // requests fails here with a generic "error occurred while sending the
            // request" — not a real server error, just a dead socket the pool handed
            // us. One retry opens a brand-new connection and, since the server itself
            // is fine, almost always succeeds immediately. This is what was surfacing
            // as repeated "Agentic error: request failed" during the agent loop.
            using var retryRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            resp = await _http.SendAsync(retryRequest, cts.Token);
        }

        using var _ = resp;
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Server returned {(int)resp.StatusCode}: {err.Truncate(500)}");
        }

        var body = await resp.Content.ReadAsStringAsync();
        if (tools is { Count: > 0 })
            WriteDiagLog("Kobold RESPONSE", body);

        if (string.IsNullOrWhiteSpace(body) || body.Trim() == "null")
            return new ChatCompletionResponse();

        try
        {
            var result = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonWeb);
            return result ?? new ChatCompletionResponse();
        }
        catch (JsonException ex)
        {
            // Surface the real cause instead of returning an indistinguishable-from-empty
            // response, which previously drove the agent loop into an infinite
            // "Empty response received" retry until context ran out.
            throw new HttpRequestException($"Failed to parse chat completion response: {ex.Message}\nRaw body: {body.Truncate(500)}");
        }
    }

    public async Task<string> SendMultimodalChatAsync(IEnumerable<ChatMessage> history, string textContent, List<string> imagePaths, int maxTokens = 65536,
        float? temperature = null, float? topP = null, int? topK = null, float? repeatPenalty = null,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("max_tokens", maxTokens);
            writer.WriteNumber("temperature", temperature ?? 0.7);
            if (topP.HasValue) writer.WriteNumber("top_p", topP.Value);
            if (topK.HasValue) writer.WriteNumber("top_k", topK.Value);
            if (repeatPenalty.HasValue) writer.WriteNumber("repeat_penalty", repeatPenalty.Value);
            writer.WriteBoolean("stream", false);
            writer.WriteStartArray("messages");

            foreach (var histMsg in history)
            {
                writer.WriteStartObject();
                writer.WriteString("role", histMsg.Role);
                writer.WriteString("content", histMsg.Content);
                writer.WriteEndObject();
            }

            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");

            foreach (var imgPath in imagePaths)
            {
                if (!File.Exists(imgPath)) continue;
                var imageBytes = await File.ReadAllBytesAsync(imgPath, ct);
                var ext = Path.GetExtension(imgPath).TrimStart('.').ToLowerInvariant();
                var mime = ext switch
                {
                    "png" => "image/png",
                    "jpg" or "jpeg" => "image/jpeg",
                    "webp" => "image/webp",
                    "gif" => "image/gif",
                    "bmp" => "image/bmp",
                    _ => "image/png"
                };
                var b64 = Convert.ToBase64String(imageBytes);
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", $"data:{mime};base64,{b64}");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", textContent);
            writer.WriteEndObject();

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(ms.ToArray());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // No timeout — generation time is unbounded (depends on hardware/context/max_tokens); only the caller's own CancellationToken can stop this.
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("/v1/chat/completions", content, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Server returned {(int)resp.StatusCode}: {errBody.Truncate(500)}");
        }
        var body = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonWeb);
        var msg = result?.Choices?.FirstOrDefault()?.Message;
        var contentText = msg?.Content;
        return string.IsNullOrWhiteSpace(contentText) ? $"(empty — model returned EOS)" : contentText;
    }

    public async Task<float[][]> GenerateEmbeddingsAsync(string[] inputs, CancellationToken ct = default)
    {
        if (inputs is not { Length: > 0 })
            return [];

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(300));

        // koboldcpp may reject the "model" field — omit it to stay compatible
        var payload = new { input = inputs };

        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonWeb), Encoding.UTF8, "application/json");
        HttpResponseMessage? resp;
        try
        {
            resp = await _http.PostAsync("/v1/embeddings", content, cts.Token);
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"Embeddings API call failed: {ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Embeddings API returned {(int)resp.StatusCode}: {errBody.Truncate(500)}");
        }

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");

        var results = new float[data.GetArrayLength()][];
        int idx = 0;
        foreach (var item in data.EnumerateArray())
        {
            var emb = item.GetProperty("embedding");
            var vec = new float[emb.GetArrayLength()];
            int i = 0;
            foreach (var val in emb.EnumerateArray())
                vec[i++] = val.GetSingle();
            results[idx++] = vec;
        }
        return results;
    }

    public async Task<string> TranscribeAudioAsync(string audioFilePath, bool translateToEnglish = false, CancellationToken ct = default)
    {
        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException("Audio file not found", audioFilePath);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(300));
        using var form = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(audioFilePath, cts.Token);
        var fileName = Path.GetFileName(audioFilePath);
        form.Add(new ByteArrayContent(fileBytes), "file", fileName);
        if (translateToEnglish)
        {
            form.Add(new StringContent("translate"), "task");
        }
        var resp = await _http.PostAsync("/v1/audio/transcriptions", form, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Server returned {(int)resp.StatusCode}: {errBody.Truncate(500)}");
        }
        var body = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AudioTranscriptionResponse>(body, JsonWeb);
        return result?.Text ?? "(no transcription)";
    }

    public async Task<byte[]> CloneVoiceAsync(string refAudioPath, string text, string? tokenizerPath = null, CancellationToken ct = default)
    {
        if (!File.Exists(refAudioPath))
            throw new FileNotFoundException("Reference audio file not found", refAudioPath);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty", nameof(text));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(300));
        // The server loads voices from --ttsdir at startup into its voicebank keyed by filename
        // The lookup uses the raw voice field value, so we pass the full filename with extension
        var voiceName = Path.GetFileName(refAudioPath);
        var payload = new Dictionary<string, object>
        {
            ["text"] = text,
            ["voice"] = voiceName,
        };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("/api/extra/tts", content, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Server returned {(int)resp.StatusCode}: {errBody.Truncate(500)}");
        }
        return await resp.Content.ReadAsByteArrayAsync();
    }

    private async Task<string> PostAndGetStringAsync(string path, object payload, string label, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(310));
        try
        {
            var resp = await _http.PostAsJsonAsync(path, payload, cts.Token);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"{label} endpoint returned {(int)resp.StatusCode}: {body.Truncate(500)}");

            return body;
        }
        catch (TaskCanceledException)
        {
            throw new HttpRequestException($"{label} request timed out.");
        }
    }

    public async Task AbortGenerationAsync(CancellationToken ct = default)
    {
        try
        {
            await PostAndGetStringAsync("/api/extra/abort", new { }, "Abort", ct);
        }
        catch { }
    }

    public async Task<List<string>> DataListAsync(CancellationToken ct = default)
    {
        var body = await PostAndGetStringAsync("/api/extra/data/list", new { }, "Data list", ct);
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.EnumerateArray().Select(r => r.GetString() ?? "").Where(s => s != "").ToList();
        return arr;
    }

    public async Task DataSaveAsync(string slot, string title, string data, CancellationToken ct = default)
    {
        var payload = new { slot, title, format = "json", data };
        await PostAndGetStringAsync("/api/extra/data/save", payload, "Data save", ct);
    }

    public async Task<string?> DataLoadAsync(string slot, CancellationToken ct = default)
    {
        var payload = new { slot };
        var body = await PostAndGetStringAsync("/api/extra/data/load", payload, "Data load", ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data))
            return data.GetString();
        return null;
    }

    public async Task<byte[]> TextToSpeechAsync(string text, string? voice = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty", nameof(text));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        var payload = new Dictionary<string, object> { ["text"] = text };
        if (!string.IsNullOrWhiteSpace(voice))
            payload["voice"] = voice;
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("/api/extra/tts", content, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"TTS returned {(int)resp.StatusCode}: {errBody.Truncate(500)}");
        }
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public async Task<string> MusicPrepareAsync(string prompt, CancellationToken ct = default)
    {
        var payload = new { prompt };
        return await PostAndGetStringAsync("/api/extra/music/prepare", payload, "Music prepare", ct);
    }

    public async Task<byte[]> MusicGenerateAsync(string codes, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(180));
        var payload = new { codes };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("/api/extra/music/generate", content, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Music generate returned {(int)resp.StatusCode}: {errBody.Truncate(500)}");
        }
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public async Task<bool> ClearStateAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync("/api/admin/clear_state", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _endpointDiscoveryLock.Dispose();
    }
}

public sealed record ImageGenerationRequest
{
    public string Prompt { get; init; } = "";
    public string? NegativePrompt { get; init; }
    public int Width { get; init; } = 1024;
    public int Height { get; init; } = 1024;
    public int Steps { get; init; } = 20;
    public float CfgScale { get; init; } = 7f;
    public long Seed { get; init; } = -1;
    public IReadOnlyList<string>? InitImagesBase64 { get; init; }
    public float DenoisingStrength { get; init; } = 0.75f;
}

public sealed record ImageGenerationResult
{
    public string ImageBase64 { get; init; } = "";
    public long Seed { get; init; }
    public string Prompt { get; init; } = "";
}

internal sealed class OpenAIImageResponse
{
    [JsonPropertyName("data")]
    public List<OpenAIImageData>? Data { get; set; }
}

internal sealed class OpenAIImageData
{
    [JsonPropertyName("b64_json")]
    public string? B64Json { get; set; }
}

internal sealed class A1111ImageResponse
{
    [JsonPropertyName("images")]
    public string[]? Images { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("info")]
    public string? Info { get; set; }
}

public sealed record VideoGenerationRequest
{
    public string Prompt { get; init; } = "";
    public string? NegativePrompt { get; init; }
    public int Width { get; init; } = 512;
    public int Height { get; init; } = 512;
    public int Steps { get; init; } = 15;
    public float CfgScale { get; init; } = 1f;
    public long Seed { get; init; } = -1;
    public int Frames { get; init; } = 50;
    public int Fps { get; init; } = 16;
    public string OutputFormat { get; init; } = "webm";
}

public sealed record VideoGenerationResult
{
    public string VideoBase64 { get; init; } = "";
    public string MimeType { get; init; } = "video/webm";
    public string OutputFormat { get; init; } = "webm";
    public int Fps { get; init; } = 16;
    public int FrameCount { get; init; }
    public long Seed { get; init; }
    public string Prompt { get; init; } = "";
    public string? SavedFilePath { get; set; }
}

internal sealed class SdcppCapabilities
{
    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; set; }
}

internal sealed class SdcppJobResult
{
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("fps")]
    public int Fps { get; set; }

    [JsonPropertyName("frame_count")]
    public int FrameCount { get; set; }

    [JsonPropertyName("b64_json")]
    public string? B64Json { get; set; }
}

internal sealed class SdcppJobResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("result")]
    public SdcppJobResult? Result { get; set; }

    [JsonPropertyName("queue_position")]
    public int QueuePosition { get; set; }
}

public sealed record BoundingBox
{
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
    public string? Original { get; init; }
    public string? Translated { get; init; }
}

internal sealed class BoundingBoxRaw
{
    [JsonPropertyName("x1")] public double X1 { get; set; }
    [JsonPropertyName("y1")] public double Y1 { get; set; }
    [JsonPropertyName("x2")] public double X2 { get; set; }
    [JsonPropertyName("y2")] public double Y2 { get; set; }
    [JsonPropertyName("original")] public string? Original { get; set; }
    [JsonPropertyName("translated")] public string? Translated { get; set; }
}

public sealed record AttachmentInfo
{
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsImage { get; init; }
    public string Icon { get; init; } = "\U0001F4C4";
}

public sealed record ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    private string? _content = "";
    [JsonPropertyName("content")]
    public string? Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Content)));
        }
    }

    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull | JsonIgnoreCondition.WhenWritingDefault)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    private string? _imagePath;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImagePath
    {
        get => _imagePath;
        set
        {
            if (_imagePath == value) return;
            _imagePath = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ImagePath)));
        }
    }

    // ObservableCollection (not List) so an ItemsControl bound to this property
    // picks up attachments added after the message was already rendered — e.g.
    // an agentic attach_file tool call that completes mid-turn. A plain List
    // mutation wouldn't raise CollectionChanged and the chip would never appear.
    [JsonIgnore]
    public ObservableCollection<AttachmentInfo>? Attachments { get; init; }

    private bool _isCollapsible;
    [JsonIgnore]
    public bool IsCollapsible
    {
        get => _isCollapsible;
        set
        {
            if (_isCollapsible == value) return;
            _isCollapsible = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCollapsible)));
        }
    }

    private bool _isExpanded;
    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    // Clickable confirmation options for the ask_user tool (manual confirm mode).
    // An ItemsControl binds to this; clicking a button invokes OptionChosen then
    // clears Options so the buttons disappear. Never serialized to the model.
    private ObservableCollection<string>? _options;
    [JsonIgnore]
    public ObservableCollection<string>? Options
    {
        get => _options;
        set
        {
            if (_options == value) return;
            _options = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Options)));
        }
    }

    [JsonIgnore]
    public Action<string>? OptionChosen { get; set; }

    public void AppendContent(string append)
    {
        _content = (_content ?? "") + append;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Content)));
    }
}

// ── Tool-calling types (OpenAI-compatible) ──

public sealed class FunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
    [JsonPropertyName("parameters")]
    public object Parameters { get; set; } = new { };
}

public sealed class ToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";
    [JsonPropertyName("function")]
    public FunctionDefinition Function { get; set; } = new();
}

public sealed class ToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";
    [JsonPropertyName("function")]
    public ToolCallFunction Function { get; set; } = new();
}

public sealed class ToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    // koboldcpp's --jinjatools native tool output serializes "arguments" as a raw JSON
    // object (per the model's own template), not the OpenAI-spec JSON-encoded string.
    // A plain `string` property here throws JsonException on every tool-call response
    // from that mode, which was being swallowed upstream and surfacing as an endless
    // "Empty response received" loop. This converter accepts either shape and always
    // normalizes to a JSON string so the rest of the app (which expects a string) never
    // has to care which backend produced it.
    [JsonPropertyName("arguments")]
    [JsonConverter(typeof(ArgumentsAsStringConverter))]
    public string Arguments { get; set; } = "";
}

public sealed class ArgumentsAsStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() ?? "";

        // Object (or array/number/bool) form: re-serialize the raw token back to a
        // JSON string so callers that expect Arguments to be a JSON-encoded string
        // (JsonDocument.Parse, etc.) keep working unchanged.
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public sealed record WebSearchResult
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";
    [JsonPropertyName("desc")]
    public string Description { get; init; } = "";
    [JsonPropertyName("content")]
    public string Content { get; init; } = "";
}

public sealed record VisionChatMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public string? ImagePath { get; init; }
}

internal sealed class ChatCompletionRequest
{
    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 512;
    [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.7f;
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; set; }
    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; set; }
    [JsonPropertyName("top_k")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopK { get; set; }
    [JsonPropertyName("repeat_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? RepeatPenalty { get; set; }
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolDefinition>? Tools { get; set; }
    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; set; }
}

public sealed class ChatCompletionChoice
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }
    [JsonPropertyName("delta")]
    public ChatMessage? Delta { get; set; }
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class ChatCompletionUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }
}

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<ChatCompletionChoice>? Choices { get; set; }
    [JsonPropertyName("usage")]
    public ChatCompletionUsage? Usage { get; set; }
}

internal sealed class AudioTranscriptionResponse
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal static partial class StringExtensions
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : string.Create(max + 3, (s, max), static (span, state) =>
        {
            state.s.AsSpan(0, state.max).CopyTo(span);
            "...".AsSpan().CopyTo(span[state.max..]);
        });
}