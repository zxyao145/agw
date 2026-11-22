using System.Net;
using System.Net.Sockets;
using Agw.Shared.Exceptions;

namespace Agw.Shared.Net;

public static class PrivateNetworkAddresses
{
    public static async Task<IPAddress[]> ResolveAsync(
        string host,
        bool allowPrivateNetworks,
        string prohibitedAddressMessage,
        CancellationToken cancellationToken
    )
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0 || !allowPrivateNetworks && addresses.Any(IsPrivate))
        {
            throw new AgwException(ErrorCodes.InvalidParam, prohibitedAddressMessage);
        }
        return addresses;
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] >= 224;
        }

        return address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || (bytes[0] & 0xfe) == 0xfc;
    }
}
