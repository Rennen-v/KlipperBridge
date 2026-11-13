using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KlipperBridge.Networking;

public sealed class WindowsLanAdapterProvider : ILanAdapterProvider
{
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct InterfaceRow
    {
        [FieldOffset(8)] public uint Index;
        [FieldOffset(1152)] public byte Flags;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfEntry2(ref InterfaceRow row);

    public IReadOnlyList<LanAdapter> GetAdapters()
    {
        var adapters = new List<LanAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211) ||
                nic.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)) continue;
            var properties = nic.GetIPProperties();
            var ipv4 = properties.GetIPv4Properties();
            if (ipv4 is null || !IsHardwareInterface(ipv4.Index)) continue;
            foreach (var unicast in properties.UnicastAddresses)
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.PrefixLength > 0 &&
                    !IPAddress.IsLoopback(unicast.Address) && !unicast.Address.GetAddressBytes().AsSpan().StartsWith(new byte[] { 169, 254 }))
                    adapters.Add(new LanAdapter(nic.Name, ipv4.Index, unicast.Address, unicast.PrefixLength));
        }
        return adapters;
    }

    private static bool IsHardwareInterface(int index)
    {
        var row = new InterfaceRow { Index = (uint)index };
        return GetIfEntry2(ref row) == 0 && (row.Flags & 1) != 0;
    }
}

public sealed class WindowsTcpSocketConfigurator : ITcpSocketConfigurator
{
    private const SocketOptionName IpUnicastInterface = (SocketOptionName)31;

    public void BindToAdapter(Socket socket, LanAdapter adapter)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("IP_UNICAST_IF is Windows-specific in this transport.");
        // Windows expects the 4-byte interface index in network byte order.
        socket.SetSocketOption(SocketOptionLevel.IP, IpUnicastInterface, IPAddress.HostToNetworkOrder(adapter.Index));
        socket.Bind(new IPEndPoint(adapter.Address, 0));
    }
}
