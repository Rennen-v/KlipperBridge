using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Features;
using KlipperBridge.Networking;

public sealed class ProxyHost : IAsyncDisposable
{
    private WebApplication? _app;
    private BridgeSettings? _settings;
    public bool IsRunning => _app is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {var builder = WebApplication.CreateBuilder(Array.Empty<string>());
var settings = builder.Configuration.GetSection("KlipperBridge").Get<BridgeSettings>()
    ?? throw new InvalidOperationException("Missing KlipperBridge configuration.");

if (!IPAddress.TryParse(settings.ListenAddress, out var listenAddress) || !IPAddress.IsLoopback(listenAddress))
    throw new InvalidOperationException("KlipperBridge must listen on a loopback address only.");
if (!Uri.TryCreate(settings.MoonrakerBaseUrl, UriKind.Absolute, out var moonrakerBase)
    || (moonrakerBase.Scheme != Uri.UriSchemeHttp && moonrakerBase.Scheme != Uri.UriSchemeHttps))
    throw new InvalidOperationException("MoonrakerBaseUrl must be an absolute HTTP(S) URL.");
if (!Uri.TryCreate(settings.FluiddBaseUrl, UriKind.Absolute, out var fluiddBase)
    || (fluiddBase.Scheme != Uri.UriSchemeHttp && fluiddBase.Scheme != Uri.UriSchemeHttps))
    throw new InvalidOperationException("FluiddBaseUrl must be an absolute HTTP(S) URL.");

builder.WebHost.ConfigureKestrel(options => options.Listen(listenAddress, settings.ListenPort));
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = long.MaxValue);
builder.Services.AddSingleton<ITcpTransport>(_ => new TcpTransport(new WindowsLanAdapterProvider(), new WindowsTcpSocketConfigurator()));
builder.Services.AddHttpClient("moonraker", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(serviceProvider => CreateAdapterAwareHandler(serviceProvider.GetRequiredService<ITcpTransport>()));
builder.Services.AddHttpClient("fluidd", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(serviceProvider => CreateAdapterAwareHandler(serviceProvider.GetRequiredService<ITcpTransport>()));

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.Run(async context =>
{
    if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/health")
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { status = "ok", fluidd = fluiddBase.ToString(), moonraker = moonrakerBase.ToString() });
        return;
    }

    if (context.WebSockets.IsWebSocketRequest)
    {
        try
        {
            await RelayWebSocketAsync(context, moonrakerBase, context.RequestServices.GetRequiredService<ITcpTransport>());
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException or IOException)
        {
            app.Logger.LogWarning(exception, "Could not connect to Moonraker WebSocket at {Target}", moonrakerBase);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        return;
    }

    try
    {
        var moonrakerRequest = ProxyRouteSelector.IsMoonrakerPath(context.Request.Path);
        var upstream = moonrakerRequest ? moonrakerBase : fluiddBase;
        var clientName = moonrakerRequest ? "moonraker" : "fluidd";
        await ProxyHttpAsync(context, upstream, app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName));
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
    {
        app.Logger.LogWarning(exception, "Could not reach upstream for {Path}", context.Request.Path);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "UpstreamUnavailable",
                message = "The proxy could not connect to the selected upstream. Check printer reachability and the approved network/VPN policy."
            });
        }
    }
});

_settings = settings;
        try
        {
            await app.StartAsync(cancellationToken);
            _app = app;
        }
        catch
        {
            await app.DisposeAsync();
            _settings = null;
            throw;
        }
    }

static async Task ProxyHttpAsync(HttpContext context, Uri baseUri, HttpClient client)
{
    var targetUri = BuildTargetUri(baseUri, context.Request);
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
    if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        request.Content = new StreamContent(context.Request.Body);
        CopyHeaders(context.Request.Headers, request.Headers, request.Content.Headers);
    }
    CopyHeaders(context.Request.Headers, request.Headers, request.Content?.Headers);

    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    context.Response.StatusCode = (int)response.StatusCode;
    CopyResponseHeaders(response, context.Response);
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
}

static async Task RelayWebSocketAsync(HttpContext context, Uri baseUri, ITcpTransport transport)
{
    using var upstream = new ClientWebSocket();
    using var handler = CreateAdapterAwareHandler(transport);
    using var invoker = new HttpMessageInvoker(handler);
    foreach (var header in context.Request.Headers)
    {
        if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            upstream.Options.SetRequestHeader(header.Key, header.Value.ToString());
    }

    var target = BuildTargetUri(baseUri, context.Request);
    var upstreamUri = new UriBuilder(target)
    {
        Scheme = target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
    }.Uri;

    await upstream.ConnectAsync(upstreamUri, invoker, context.RequestAborted);
    using var downstream = await context.WebSockets.AcceptWebSocketAsync();
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var toUpstream = PumpAsync(downstream, upstream, linked.Token);
    var toDownstream = PumpAsync(upstream, downstream, linked.Token);
    await Task.WhenAny(toUpstream, toDownstream);
    linked.Cancel();
    try { await Task.WhenAll(toUpstream, toDownstream); }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
}

static SocketsHttpHandler CreateAdapterAwareHandler(ITcpTransport transport) => new()
{
    UseProxy = false,
    ConnectCallback = async (connection, cancellationToken) =>
    {
        var endpoint = connection.DnsEndPoint;
        var address = IPAddress.TryParse(endpoint.Host, out var literal)
            ? literal
            : (await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new SocketException((int)SocketError.HostNotFound);
        var client = await transport.ConnectAsync(address, endpoint.Port, TimeSpan.FromSeconds(5), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        try { return new TcpClientOwnedStream(client, client.GetStream()); }
        catch { client.Dispose(); throw; }
    }
};

static async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
{
    var buffer = new byte[32 * 1024];
    while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
    {
        var result = await source.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            if (destination.State == WebSocketState.Open)
                await destination.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    result.CloseStatusDescription, CancellationToken.None);
            return;
        }
        await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
    }
}

static Uri BuildTargetUri(Uri baseUri, HttpRequest request)
{
    var baseText = baseUri.ToString().TrimEnd('/');
    var path = request.PathBase.Add(request.Path).ToUriComponent();
    return new Uri(baseText + path + request.QueryString, UriKind.Absolute);
}

static void CopyHeaders(IHeaderDictionary source, System.Net.Http.Headers.HttpRequestHeaders requestHeaders,
    System.Net.Http.Headers.HttpContentHeaders? contentHeaders)
{
    var hopByHop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host" };
    foreach (var header in source)
    {
        if (hopByHop.Contains(header.Key)) continue;
        if (!requestHeaders.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            contentHeaders?.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    }
}

static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
{
    foreach (var header in source.Headers)
        if (!header.Key.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
            destination.Headers[header.Key] = header.Value.ToArray();
    foreach (var header in source.Content.Headers)
        if (!header.Key.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
            destination.Headers[header.Key] = header.Value.ToArray();
    destination.Headers.Remove("transfer-encoding");
}

    public async Task<bool> CheckMoonrakerAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null || _settings is null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var client = _app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("moonraker");
            using var response = await client.GetAsync(new Uri(_settings.MoonrakerBaseUrl.TrimEnd('/') + "/server/info"), timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or SocketException)
        { return false; }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var app = Interlocked.Exchange(ref _app, null);
        _settings = null;
        if (app is null) return;
        try { await app.StopAsync(cancellationToken); }
        finally { await app.DisposeAsync(); }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}

sealed class BridgeSettings
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 8765;
    public string FluiddBaseUrl { get; set; } = "http://qidi-q2.local:80";
    public string MoonrakerBaseUrl { get; set; } = "http://qidi-q2.local:7125";
}

sealed class TcpClientOwnedStream(TcpClient client, Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); client.Dispose(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        client.Dispose();
        GC.SuppressFinalize(this);
    }
}



