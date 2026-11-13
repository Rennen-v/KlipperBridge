using System.Net;

namespace KlipperBridge.Networking;

public sealed record LanAdapter(string Name, int Index, IPAddress Address, int PrefixLength)
{
    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || PrefixLength is < 0 or > 32)
            return false;
        var local = Address.GetAddressBytes();
        var remote = address.GetAddressBytes();
        var whole = PrefixLength / 8;
        var bits = PrefixLength % 8;
        for (var i = 0; i < whole; i++) if (local[i] != remote[i]) return false;
        if (bits == 0) return true;
        var mask = (byte)(0xff << (8 - bits));
        return (local[whole] & mask) == (remote[whole] & mask);
    }
}
