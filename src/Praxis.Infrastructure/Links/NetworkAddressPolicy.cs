using System.Net;
using System.Net.Sockets;

namespace Praxis.Infrastructure.Links;

/// <summary>
/// Which addresses the link fetcher may connect to: public unicast only. Loopback, private,
/// link-local, carrier-grade NAT, multicast, documentation, benchmarking and reserved ranges are
/// refused in IPv4 and IPv6 (including IPv4-mapped and NAT64-embedded forms), so a saved link can
/// never reach the Mac itself, the home network or cloud metadata services.
/// </summary>
internal static class NetworkAddressPolicy
{
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublic(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2))
                     || (b[0] == 192 && b[1] == 88 && b[2] == 99)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        byte[] v6 = address.GetAddressBytes();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast || address.IsIPv6UniqueLocal || address.IsIPv6Teredo)
        {
            return false;
        }

        // NAT64 (64:ff9b::/96) embeds an IPv4 address; judge that address.
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b)
        {
            return IsPublic(new IPAddress(v6[12..16]));
        }

        // Only global unicast (2000::/3) is public; 2001:db8::/32 is documentation.
        return (v6[0] & 0xE0) == 0x20 && !(v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8);
    }
}
