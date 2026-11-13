using System.Net;
using System.Net.Sockets;
using KlipperBridge.Networking;
using Xunit;

namespace KlipperBridge.Tests;

public sealed class TcpTransportTests
{
    private static readonly LanAdapter Lan = new("Test LAN", 7, IPAddress.Parse("192.168.10.20"), 24);

    [Fact]
    public void SelectsOnlyAdapterContainingDestination()
    {
        var other = new LanAdapter("Other", 8, IPAddress.Parse("10.0.0.2"), 24);
        var transport = Create([other, Lan]);
        Assert.Equal(Lan, transport.SelectAdapter(IPAddress.Parse("192.168.10.99")));
    }

    [Fact]
    public void RejectsAmbiguousAdaptersUnlessIndexSpecified()
    {
        var second = new LanAdapter("Second", 9, IPAddress.Parse("192.168.10.21"), 24);
        var transport = Create([Lan, second]);
        Assert.Throws<InvalidOperationException>(() => transport.SelectAdapter(IPAddress.Parse("192.168.10.99")));
        Assert.Equal(second, transport.SelectAdapter(IPAddress.Parse("192.168.10.99"), 9));
    }

    [Fact]
    public async Task ConnectsToLocalListenerAndDisposesTcpClient()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var adapter = new LanAdapter("Loopback test", 1, IPAddress.Parse("127.0.0.1"), 8);
        var transport = Create([adapter]);
        var acceptedTask = listener.AcceptTcpClientAsync();
        var client = await transport.ConnectAsync(IPAddress.Loopback, endpoint.Port, TimeSpan.FromSeconds(2));
        using (client)
        {
            Assert.True(client.Connected);
            using var accepted = await acceptedTask;
            Assert.True(accepted.Connected);
        }
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task ClosesSocketWhenConnectionIsRefused()
    {
        var configurator = new CapturingSocketConfigurator();
        var transport = CreateTransport(configurator, new RefusingConnectionAttempt());
        var exception = await Assert.ThrowsAsync<SocketException>(() => transport.ConnectAsync(IPAddress.Loopback, 12345, TimeSpan.FromSeconds(2)));
        Assert.Equal(SocketError.ConnectionRefused, exception.SocketErrorCode);
        Assert.True(configurator.Socket!.SafeHandle.IsClosed);
    }

    [Fact]
    public async Task TimesOutAndDisposesSocket()
    {
        var configurator = new CapturingSocketConfigurator();
        var transport = CreateTransport(configurator, new WaitingConnectionAttempt());
        await Assert.ThrowsAsync<TimeoutException>(() => transport.ConnectAsync(IPAddress.Loopback, 12345, TimeSpan.FromMilliseconds(50)));
        Assert.True(configurator.Socket!.SafeHandle.IsClosed);
    }

    [Fact]
    public async Task DisposesSocketWhenConfigurationFails()
    {
        var configurator = new ThrowingSocketConfigurator();
        var transport = CreateTransport(configurator, new WaitingConnectionAttempt());
        await Assert.ThrowsAsync<SocketException>(() => transport.ConnectAsync(IPAddress.Loopback, 12345, TimeSpan.FromSeconds(1)));
        Assert.True(configurator.Socket!.SafeHandle.IsClosed);
    }

    [Fact]
    public async Task HonorsCallerCancellationAndDisposesSocket()
    {
        var configurator = new CapturingSocketConfigurator();
        var transport = new TcpTransport(new FixedAdapterProvider([new LanAdapter("Loopback test", 1, IPAddress.Loopback, 8)]), configurator);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, TimeSpan.FromSeconds(1), cancellationToken: cancellation.Token));
        Assert.True(configurator.Socket!.SafeHandle.IsClosed);
    }

    private static TcpTransport Create(IReadOnlyList<LanAdapter> adapters) =>
        new(new FixedAdapterProvider(adapters), new NoopSocketConfigurator());

    private static TcpTransport CreateTransport(ITcpSocketConfigurator configurator, ITcpConnectionAttempt connectionAttempt) =>
        new(new FixedAdapterProvider([new LanAdapter("Loopback test", 1, IPAddress.Loopback, 8)]), configurator, connectionAttempt);

    private sealed class FixedAdapterProvider(IReadOnlyList<LanAdapter> adapters) : ILanAdapterProvider
    { public IReadOnlyList<LanAdapter> GetAdapters() => adapters; }

    private sealed class NoopSocketConfigurator : ITcpSocketConfigurator
    { public void BindToAdapter(Socket socket, LanAdapter adapter) => socket.Bind(new IPEndPoint(adapter.Address, 0)); }

    private sealed class CapturingSocketConfigurator : ITcpSocketConfigurator
    {
        public Socket? Socket { get; private set; }
        public void BindToAdapter(Socket socket, LanAdapter adapter)
        { Socket = socket; socket.Bind(new IPEndPoint(adapter.Address, 0)); }
    }

    private sealed class ThrowingSocketConfigurator : ITcpSocketConfigurator
    {
        public Socket? Socket { get; private set; }
        public void BindToAdapter(Socket socket, LanAdapter adapter)
        { Socket = socket; throw new SocketException((int)SocketError.AccessDenied); }
    }

    private sealed class RefusingConnectionAttempt : ITcpConnectionAttempt
    {
        public Task ConnectAsync(TcpClient client, IPAddress destination, int port, CancellationToken cancellationToken) =>
            Task.FromException(new SocketException((int)SocketError.ConnectionRefused));
    }

    private sealed class WaitingConnectionAttempt : ITcpConnectionAttempt
    {
        public Task ConnectAsync(TcpClient client, IPAddress destination, int port, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
