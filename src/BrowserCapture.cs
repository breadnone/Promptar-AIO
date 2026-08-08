using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyAiGen;

public static class BrowserCapture
{
    public static string? FindBrowser()
    {
        // Edge is always available on Windows — check it first
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var edgeX86 = Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe");
        if (File.Exists(edgeX86)) return edgeX86;

        var edgeLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe");
        if (File.Exists(edgeLocal)) return edgeLocal;

        var programFiles64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var edge64 = Path.Combine(programFiles64, "Microsoft", "Edge", "Application", "msedge.exe");
        if (File.Exists(edge64)) return edge64;

        // Chrome
        var chromeX86 = Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
        if (File.Exists(chromeX86)) return chromeX86;
        var chromeLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe");
        if (File.Exists(chromeLocal)) return chromeLocal;

        // Brave
        var braveX86 = Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
        if (File.Exists(braveX86)) return braveX86;

        return null;
    }

    public static (bool Success, string? Error) CaptureScreenshot(string html, string outputPath, int width, int height)
    {
        var browser = FindBrowser();
        if (browser == null)
            return (false, "No Chromium-based browser found (Chrome, Edge, or Brave). Install one to use the render_html tool.");

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempHtml = Path.Combine(Path.GetTempPath(), $"render_{Guid.NewGuid():N}.html");
        var tempProfileDir = Path.Combine(Path.GetTempPath(), $"render_profile_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempHtml, html);
            var fileUrl = new Uri(tempHtml).AbsoluteUri;
            Directory.CreateDirectory(tempProfileDir);

            // Collect stderr lines in a thread-safe queue
            var stderrLines = new ConcurrentQueue<string>();
            using var chrome = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = browser,
                    // Start with about:blank so CDP navigation is the only page load.
                    // --user-data-dir is REQUIRED here: without it, Chrome treats the
                    // profile dir as a single-instance lock. If any Chrome window is
                    // already open on the machine, this invocation doesn't start a new
                    // process at all - it silently hands the command off to the running
                    // instance and exits immediately, so no "DevTools listening on ws://"
                    // line is ever printed and WaitForDevToolsUrl times out. A private,
                    // per-capture profile dir guarantees an isolated new process every
                    // time regardless of what else is running.
                    // --headless=new is the modern flag; the old bare --headless mode
                    // was removed in recent Chrome/Edge releases.
                    Arguments = $"--headless --disable-gpu --remote-debugging-port=0 " +
                                $"--user-data-dir=\"{tempProfileDir}\" --no-first-run --no-default-browser-check " +
                                $"about:blank",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            chrome.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderrLines.Enqueue(e.Data);
            };

            chrome.Start();
            chrome.BeginErrorReadLine();

            var wsUrl = WaitForDevToolsUrl(stderrLines, chrome, 30000);
            if (wsUrl == null)
            {
                var allStderr = string.Join("\n", stderrLines);
                try { chrome.Kill(entireProcessTree: true); } catch { }
                return (false, $"Failed to get DevTools WebSocket URL. Stderr ({allStderr.Length} chars): {allStderr.Truncate(500)}");
            }

            var result = CaptureViaCdp(wsUrl, fileUrl, outputPath, width, height, 20000);

            try { chrome.Kill(entireProcessTree: true); } catch { }

            if (!result.Success)
                return (false, result.Error);

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                return (false, "Browser produced empty output file.");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Browser render error: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempHtml); } catch { }
            try { Directory.Delete(tempProfileDir, recursive: true); } catch { }
        }
    }

    private static string? WaitForDevToolsUrl(ConcurrentQueue<string> stderrLines, Process chrome, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            // Collect all lines into a single string for searching
            var lines = string.Join("\n", stderrLines);
            var idx = lines.IndexOf("DevTools listening on ws://", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var start = idx + "DevTools listening on ws://".Length;
                var end = lines.IndexOfAny(['\n', '\r'], start);
                if (end < 0) end = lines.Length;
                var urlPart = lines[start..end].Trim();
                return $"ws://{urlPart}";
            }

            if (chrome.HasExited)
                return null;

            Thread.Sleep(50);
        }

        return null;
    }

    private static (bool Success, string? Error) CaptureViaCdp(
        string wsUrl, string fileUrl, string outputPath, int width, int height, int timeoutMs)
    {
        using var ws = new ClientWebSocket();
        var cts = new CancellationTokenSource(timeoutMs);
        var ct = cts.Token;

        try
        {
            ws.ConnectAsync(new Uri(wsUrl), ct).GetAwaiter().GetResult();

            // Enable Page domain
            SendCdpAndWait(ws, 1, "Page.enable", null, ct);

            // Set generous viewport so content at any coordinate is rendered
            var vpWidth = Math.Max(width, 1920);
            var vpHeight = Math.Max(height, 1080);
            SendCdpAndWait(ws, 2, "Emulation.setDeviceMetricsOverride", new
            {
                width = vpWidth,
                height = vpHeight,
                deviceScaleFactor = 1,
                mobile = false
            }, ct);

            // Navigate via CDP (page starts at about:blank so this is the first real load)
            var navMsg = JsonSerializer.Serialize(new
            {
                id = 3,
                method = "Page.navigate",
                @params = new { url = fileUrl }
            });
            SendRaw(ws, navMsg, ct);

            // Wait for Page.loadEventFired
            if (!WaitForEvent(ws, "Page.loadEventFired", ct, 10000))
                return (false, "Page load timed out.");

            // Capture screenshot with captureBeyondViewport to avoid clipping
            var screenshotMsg = JsonSerializer.Serialize(new
            {
                id = 100,
                method = "Page.captureScreenshot",
                @params = new
                {
                    format = "png",
                    captureBeyondViewport = true,
                    fromSurface = true
                }
            });
            SendRaw(ws, screenshotMsg, ct);

            var base64Data = WaitForResult(ws, 100, ct, 15000);
            if (base64Data == null)
                return (false, "No screenshot data received from browser.");

            var raw = Convert.FromBase64String(base64Data);
            File.WriteAllBytes(outputPath, raw);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"CDP error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a CDP command and consumes all responses/events until a message
    /// with the matching id comes back (confirms delivery, avoids queue buildup).
    /// </summary>
    private static void SendCdpAndWait(ClientWebSocket ws, int id, string method, object? paramsObj, CancellationToken ct)
    {
        object msg = paramsObj != null
            ? new { id, method, @params = paramsObj }
            : new { id, method };

        var json = JsonSerializer.Serialize(msg);
        SendRaw(ws, json, ct);

        // Drain messages until we get our response
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            var deadline = Environment.TickCount + 5000;

            while (Environment.TickCount < deadline)
            {
                var response = ReadMessage(ws, buffer, ct);
                if (response == null) break;

                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(response);
                    if (doc.TryGetProperty("id", out var rid) && rid.GetInt32() == id)
                        return;
                }
                catch { }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void SendRaw(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Reads CDP messages in a loop until one matches the given event name.
    /// </summary>
    private static bool WaitForEvent(ClientWebSocket ws, string eventName, CancellationToken ct, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
            {
                if (ws.State != WebSocketState.Open)
                    return false;

                var json = ReadMessage(ws, buffer, ct);
                if (json == null) continue;

                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);
                    if (doc.TryGetProperty("method", out var m) && m.GetString() == eventName)
                        return true;
                    // frameStoppedLoading is also a valid load-complete signal
                    if (doc.TryGetProperty("method", out var m2) && m2.GetString() == "Page.frameStoppedLoading")
                        return true;
                }
                catch { }
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads CDP messages until a response with the given id arrives.
    /// Returns the "data" field from the result (for captureScreenshot) or null.
    /// </summary>
    private static string? WaitForResult(ClientWebSocket ws, int expectedId, CancellationToken ct, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var buffer = ArrayPool<byte>.Shared.Rent(2_000_000);
        try
        {
            while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
            {
                var json = ReadMessage(ws, buffer, ct);
                if (json == null) continue;

                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);
                    if (!doc.TryGetProperty("id", out var idEl) || idEl.GetInt32() != expectedId)
                        continue;

                    if (doc.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("data", out var data))
                        return data.GetString();

                    if (doc.TryGetProperty("error", out var errEl))
                    {
                        var msg = errEl.TryGetProperty("message", out var mEl) ? mEl.GetString() : "unknown CDP error";
                        throw new Exception(msg);
                    }

                    return null;
                }
                catch { }
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a single JSON message from the WebSocket. Blocks for up to ~100ms
    /// waiting for data, then returns null if nothing arrived.
    /// </summary>
    private static string? ReadMessage(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return null;

        var segment = new ArraySegment<byte>(buffer);
        var receiveTask = ws.ReceiveAsync(segment, ct);

        // Poll with short timeout so we don't block too long
        if (!receiveTask.Wait(100)) return null;

        var result = receiveTask.Result;
        if (result.MessageType == WebSocketMessageType.Close) return null;

        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        if (!result.EndOfMessage)
        {
            var sb = new StringBuilder(json);
            while (!result.EndOfMessage)
            {
                result = ws.ReceiveAsync(segment, ct).GetAwaiter().GetResult();
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            json = sb.ToString();
        }

        return json;
    }
}