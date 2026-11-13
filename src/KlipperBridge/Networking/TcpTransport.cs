using System.Net;
using System.Net.Sockets;

namespace KlipperBridge.Networking;

public sealed class TcpTransport : ITcpTransport
{
    private readonly ILanAdapterProvider _adapters;
    private readonly ITcpSocketConfigurator _configurator;
    private readonly ITcpConnectionAttempt _connectionAttempt;

    public TcpTransport(ILanAdapterProvider adapters, ITcpSocketConfigurator configurator, ITcpConnectionAttempt? connectionAttempt = null)
    { _adapters = adapters; _configurator = configurator; _connectionAttempt = connectionAttempt ?? new TcpConnectionAttempt(); }

    public IReadOnlyList<LanAdapter> GetAdapters() => _adapters.GetAdapters();

    public LanAdapter SelectAdapter(IPAddress destination, int? interfaceIndex = null)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("IPv4 destination required.", nameof(destination));
        var candidates = GetAdapters().Where(a => a.Contains(destination) && (interfaceIndex is null || a.Index == interfaceIndex)).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException("No eligible physical IPv4 adapter has the destination in its subnet."),
            _ => throw new InvalidOperationException("More than one adapter matches; specify an interface index explicitly.")
        };
    }

    public async Task<TcpClient> ConnectAsync(IPAddress destination, int port, TimeSpan timeout,
        int? interfaceIndex = null, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var adapter = SelectAdapter(destination, interfaceIndex);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            _configurator.BindToAdapter(client.Client, adapter);
            await _connectionAttempt.ConnectAsync(client, destination, port, deadline.Token).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"TCP connection to {destination}:{port} exceeded {timeout}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal sealed class TcpConnectionAttempt : ITcpConnectionAttempt
{
    public async Task ConnectAsync(TcpClient client, IPAddress destination, int port, CancellationToken cancellationToken) =>
        await client.ConnectAsync(destination, port, cancellationToken).ConfigureAwait(false);
}
