using System.Net;
using System.Text;
using System.Text.Json;

namespace PixelBarLyrics;

/// <summary>
/// 外部字幕推送处理结果
/// </summary>
public sealed record ExternalTextResult(bool Accepted, bool Sent, string ScreenText);

/// <summary>
/// 外部接口状态快照
/// </summary>
public sealed record ServerStatusSnapshot(
    bool Connected,
    bool Paused,
    bool ExternalActive,
    string LastText);

/// <summary>
/// 本地 HTTP 字幕接收服务（仅绑定 127.0.0.1，供浏览器油猴脚本 / MPV 等外部播放器推送字幕）
/// </summary>
public sealed class LocalSubtitleServer : IDisposable
{
    private readonly int _port;
    private readonly Func<string, string, int, ExternalTextResult> _onPushText;
    private readonly Action _onClear;
    private readonly Func<ServerStatusSnapshot> _getStatus;
    private readonly Action<string> _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public bool IsRunning { get; private set; }
    public int Port => _port;

    public LocalSubtitleServer(
        int port,
        Func<string, string, int, ExternalTextResult> onPushText,
        Action onClear,
        Func<ServerStatusSnapshot> getStatus,
        Action<string>? log = null)
    {
        _port = port;
        _onPushText = onPushText;
        _onClear = onClear;
        _getStatus = getStatus;
        _log = log ?? (_ => { });
    }

    public bool Start()
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            listener.Start();

            _listener = listener;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _log($"本地视频字幕 HTTP 接口已启动: http://127.0.0.1:{_port}/");
            return true;
        }
        catch (Exception ex)
        {
            _log($"本地视频字幕 HTTP 接口启动失败（不影响音乐同步）: {ex.Message}");
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        try { _acceptTask?.Wait(500); } catch { }
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptTask = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"HTTP 接收连接异常: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(ctx), ct);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse resp = ctx.Response;

        ApplyCorsHeaders(resp);

        try
        {
            if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            string path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
            if (string.IsNullOrEmpty(path)) path = "/";

            if (string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) && path == "/status")
            {
                ServerStatusSnapshot st = _getStatus();
                await WriteJsonAsync(resp, 200, new
                {
                    ok = true,
                    port = _port,
                    connected = st.Connected,
                    paused = st.Paused,
                    externalActive = st.ExternalActive,
                    lastText = st.LastText
                }).ConfigureAwait(false);
                return;
            }

            if (string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) && path == "/text")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                string body = await reader.ReadToEndAsync().ConfigureAwait(false);

                (string text, string source, int ttlMs) = ParsePushBody(body, req.ContentType);
                if (string.IsNullOrWhiteSpace(text))
                {
                    await WriteJsonAsync(resp, 400, new { ok = false, error = "text 不能为空" }).ConfigureAwait(false);
                    return;
                }

                ttlMs = Math.Clamp(ttlMs, 1000, 30000);
                ExternalTextResult res = _onPushText(text, source, ttlMs);
                await WriteJsonAsync(resp, 200, new
                {
                    ok = res.Accepted,
                    sent = res.Sent,
                    text = res.ScreenText
                }).ConfigureAwait(false);
                return;
            }

            if (string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) && path == "/clear")
            {
                _onClear();
                await WriteJsonAsync(resp, 200, new { ok = true }).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(resp, 404, new { ok = false, error = "未知路由" }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"HTTP 请求处理异常: {ex.Message}");
            try
            {
                await WriteJsonAsync(resp, 500, new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private static (string Text, string Source, int TtlMs) ParsePushBody(string body, string? contentType)
    {
        string text = string.Empty;
        string source = "external";
        int ttlMs = 6000;

        string trimmed = body.Trim();
        bool looksJson = (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (trimmed.StartsWith('{') && trimmed.EndsWith('}'));

        if (looksJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("text", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String)
                    {
                        text = tEl.GetString() ?? string.Empty;
                    }
                    if (root.TryGetProperty("source", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.String)
                    {
                        string? s = sEl.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) source = s.Trim();
                    }
                    if (root.TryGetProperty("ttlMs", out JsonElement ttlEl) && ttlEl.ValueKind == JsonValueKind.Number)
                    {
                        ttlMs = ttlEl.GetInt32();
                    }
                }
            }
            catch
            {
                text = trimmed;
            }
        }
        else
        {
            text = trimmed;
        }

        return (text, source, ttlMs);
    }

    private static void ApplyCorsHeaders(HttpListenerResponse resp)
    {
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        resp.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        resp.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int statusCode, object payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        resp.OutputStream.Close();
    }

    public void Dispose() => Stop();
}
