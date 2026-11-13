using System.Net;
using System.Net.Sockets;

namespace KlipperBridge.Networking;

public interface ILanAdapterProvider
{
    IReadOnlyList<LanAdapter> GetAdapters();
}

public interface ITcpSocketConfigurator
{
    void BindToAdapter(Socket socket, LanAdapter adapter);
}

public interface ITcpConnectionAttempt
{
    Task ConnectAsync(TcpClient client, IPAddress destination, int port, CancellationToken cancellationToken);
}

public interface ITcpTransport
{
    IReadOnlyList<LanAdapter> GetAdapters();
    LanAdapter SelectAdapter(IPAddress destination, int? interfaceIndex = null);
    Task<TcpClient> ConnectAsync(IPAddress destination, int port, TimeSpan timeout,
        int? interfaceIndex = null, CancellationToken cancellationToken = default);
}
