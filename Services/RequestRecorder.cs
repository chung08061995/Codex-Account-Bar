using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CodexAccountBar.Services;

public sealed class RequestRecorder
{
    #region Constants

    public const string Address = "http://localhost:47629/";
    public const string BaseUrl = Address + "backend-api/codex";
    private const string Upstream = "https://chatgpt.com";

    #endregion

    #region Fields

    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase) { "Host", "Connection", "Content-Length", "Transfer-Encoding", "Keep-Alive", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate", "TE", "Trailer" };
    private readonly HttpClient _http;

    #endregion

    #region Initialization

    public RequestRecorder()
    {
        var handler = UsageHttpClient.CreateHandler();
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    #endregion

    #region Public Methods

    public async Task RunAsync()
    {
        using var singleton = new Mutex(false, "Local\\CodexAccountBar-RequestRecorder");
        bool acquired;
        try { acquired = singleton.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return;
        using var listener = new HttpListener();
        listener.Prefixes.Add(Address);
        listener.Start();
        AppLog.Info("Local request recorder started");
        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = HandleAsync(context);
        }
    }

    public static async Task<bool> IsRunningAsync()
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
            return await http.GetStringAsync(Address + "health") == "CodexAccountBar.RequestRecorder.v1";
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    public static async Task StartAsync()
    {
        if (await IsRunningAsync()) return;
        var path = Environment.ProcessPath ?? throw new IOException("Application executable is unavailable.");
        Process.Start(new ProcessStartInfo(path, "--request-recorder") { UseShellExecute = false, CreateNoWindow = true });
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (await IsRunningAsync()) return;
            await Task.Delay(100);
        }
        throw new IOException("Local request recorder could not start. Codex routing was not changed.");
    }

    #endregion

    #region Request Forwarding

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var input = context.Request;
            if (input.RemoteEndPoint is null || !IPAddress.IsLoopback(input.RemoteEndPoint.Address) || input.Headers["Origin"] is not null)
            {
                context.Response.StatusCode = 403;
                return;
            }
            var path = input.Url?.AbsolutePath ?? "";
            if (path == "/health")
            {
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("CodexAccountBar.RequestRecorder.v1"));
                return;
            }
            if (!path.StartsWith("/backend-api/codex/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }
            if (input.HttpMethod is not ("GET" or "POST") || string.IsNullOrWhiteSpace(input.Headers["Authorization"]))
            {
                context.Response.StatusCode = 400;
                return;
            }
            var target = new Uri(Upstream + path + input.Url!.Query);
            var capture = new CaptureState(input.Headers["ChatGPT-Account-ID"] ?? "", input.Headers["thread-id"] ?? input.Headers["session-id"] ?? "");
            if (input.IsWebSocketRequest)
            {
                await ForwardWebSocketAsync(context, target, capture);
                return;
            }
            using var request = new HttpRequestMessage(new HttpMethod(input.HttpMethod), target);
            if (input.HasEntityBody) request.Content = new StreamContent(input.InputStream);
            foreach (var name in input.Headers.AllKeys.OfType<string>().Where(name => !HopHeaders.Contains(name)))
                if (!request.Headers.TryAddWithoutValidation(name, input.Headers.GetValues(name) ?? [])) request.Content?.Headers.TryAddWithoutValidation(name, input.Headers.GetValues(name) ?? []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers).Where(header => !HopHeaders.Contains(header.Key)))
                context.Response.Headers[header.Key] = string.Join(", ", header.Value);
            context.Response.SendChunked = true;
            capture.RequestId = response.Headers.TryGetValues("x-oai-request-id", out var values) ? values.FirstOrDefault() ?? "" : "";
            var track = path.EndsWith("/responses", StringComparison.Ordinal) || path.EndsWith("/responses/compact", StringComparison.Ordinal);
            if (track) await capture.SaveAsync($"HTTP {(int)response.StatusCode}");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var inspect = track && response.Content.Headers.ContentType?.MediaType == "text/event-stream" && !response.Content.Headers.ContentEncoding.Any();
            if (inspect)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync(timeout.Token) is { } line)
                {
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), timeout.Token);
                    await context.Response.OutputStream.FlushAsync(timeout.Token);
                    if (line.StartsWith("data: ", StringComparison.Ordinal)) await capture.ObserveAsync(line[6..]);
                }
            }
            else await stream.CopyToAsync(context.Response.OutputStream, timeout.Token);
        }
        catch (Exception exception)
        {
            AppLog.Error("Request recorder forwarding", new IOException($"Forwarding failed: {exception.GetType().Name}"));
            try { context.Response.StatusCode = 502; }
            catch (InvalidOperationException) { }
        }
        finally
        {
            try { context.Response.Close(); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task ForwardWebSocketAsync(HttpListenerContext context, Uri target, CaptureState capture)
    {
        using var upstream = new ClientWebSocket();
        upstream.Options.CollectHttpResponseDetails = true;
        using var handler = UsageHttpClient.CreateHandler();
        upstream.Options.Proxy = handler.Proxy ?? HttpClient.DefaultProxy;
        foreach (var name in context.Request.Headers.AllKeys.OfType<string>().Where(name => !HopHeaders.Contains(name) && !name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase)))
            upstream.Options.SetRequestHeader(name, context.Request.Headers[name]!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try { await upstream.ConnectAsync(new UriBuilder(target) { Scheme = "wss", Port = 443 }.Uri, timeout.Token); }
        catch (WebSocketException) when ((int)upstream.HttpStatusCode >= 400)
        {
            context.Response.StatusCode = (int)upstream.HttpStatusCode;
            return;
        }
        var accepted = await context.AcceptWebSocketAsync(null);
        using var downstream = accepted.WebSocket;
        var outbound = RelayAsync(downstream, upstream, null, timeout.Token);
        var inbound = RelayAsync(upstream, downstream, capture, timeout.Token);
        await Task.WhenAny(outbound, inbound);
        timeout.Cancel();
        try { await Task.WhenAll(outbound, inbound); }
        catch (OperationCanceledException) { }
    }

    private static async Task RelayAsync(WebSocket source, WebSocket target, CaptureState? capture, CancellationToken token)
    {
        var buffer = new byte[65536];
        using var message = new MemoryStream();
        while (source.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (target.State == WebSocketState.Open) await target.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token);
                return;
            }
            await target.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, token);
            if (capture is null || result.MessageType != WebSocketMessageType.Text) continue;
            if (message.Length + result.Count <= 1048576) message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            await capture.ObserveAsync(Encoding.UTF8.GetString(message.ToArray()));
            message.SetLength(0);
        }
    }

    #endregion

    #region Nested Types

    private sealed class CaptureState(string accountId, string threadId)
    {
        #region Fields

        private DateTimeOffset _timestamp = DateTimeOffset.UtcNow;
        private string _model = "Unavailable";
        private string _key = Guid.NewGuid().ToString("N");
        private string _responseId = "";

        #endregion

        #region Properties

        public string RequestId { get; set; } = "";

        #endregion

        #region Public Methods

        public async Task SaveAsync(string status, long? tokens = null, long? input = null, long? output = null, long? cached = null)
        {
            try { await RequestCaptureStore.AppendAsync(new(_timestamp, accountId, threadId, _model, status, RequestId.Length == 0 ? _key : RequestId, tokens, input, output, cached, _responseId)); }
            catch (Exception exception) { AppLog.Error("Persist request metadata", new IOException(exception.GetType().Name)); }
        }

        public async Task ObserveAsync(string text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() is not ("response.created" or "response.completed" or "response.failed")) return;
                if (!root.TryGetProperty("response", out var response)) return;
                if (response.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.String) _responseId = responseId.GetString() ?? "";
                if (type.GetString() == "response.created")
                {
                    _timestamp = DateTimeOffset.UtcNow;
                    _key = response.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                }
                if (response.TryGetProperty("model", out var model)) _model = model.GetString() ?? "Unavailable";
                var usage = response.TryGetProperty("usage", out var reported) && reported.ValueKind == JsonValueKind.Object ? reported : default;
                var cached = Count(usage, "cached_input_tokens");
                if (usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens_details", out var details)) cached ??= Count(details, "cached_tokens");
                await SaveAsync(type.GetString() == "response.completed" ? "Completed" : type.GetString() == "response.failed" ? "Failed" : "Streaming", Count(usage, "total_tokens"), Count(usage, "input_tokens"), Count(usage, "output_tokens"), cached);
            }
            catch (JsonException) { }
        }

        #endregion

        #region Private Methods

        private static long? Count(JsonElement usage, string name)
            => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;

        #endregion
    }

    #endregion
}
