using System.Net;
using System.Net.Sockets;

namespace LenxTool.Core.Feeds;

public enum NetworkAddressDisposition
{
    Public,
    Private,
    SyntheticProxy,
    Forbidden
}

public static class NetworkTargetClassifier
{
    public static NetworkAddressDisposition Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return Classify(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            byte first = bytes[0];
            byte second = bytes[1];
            if (first == 10
                || (first == 172 && second is >= 16 and <= 31)
                || (first == 192 && second == 168)
                || (first == 100 && second is >= 64 and <= 127))
            {
                return NetworkAddressDisposition.Private;
            }

            if (first == 0
                || first == 127
                || first >= 224
                || (first == 169 && second == 254)
                || (first == 192 && second == 0)
                || (first == 192 && second == 2)
                || (first == 192 && second == 88 && bytes[2] == 99)
                || (first == 198 && second == 51 && bytes[2] == 100)
                || (first == 203 && second == 0 && bytes[2] == 113))
            {
                return NetworkAddressDisposition.Forbidden;
            }

            if (first == 198 && second is 18 or 19)
            {
                // Clash/sing-box Fake-IP DNS uses 198.18.0.0/15 for public
                // host names. Literal access remains distinguishable so callers
                // can require a TLS/SNI-bound public host before allowing it.
                return NetworkAddressDisposition.SyntheticProxy;
            }

            return NetworkAddressDisposition.Public;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6Loopback)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return NetworkAddressDisposition.Forbidden;
        }

        byte[] ipv6 = address.GetAddressBytes();
        if (ipv6[..12].All(value => value == 0))
        {
            return NetworkAddressDisposition.Forbidden;
        }

        if (ipv6[0] == 0x00
            && ipv6[1] == 0x64
            && ipv6[2] == 0xFF
            && ipv6[3] == 0x9B)
        {
            if (ipv6[4] == 0x00 && ipv6[5] == 0x01)
            {
                return NetworkAddressDisposition.Forbidden;
            }

            if (ipv6[4..12].All(value => value == 0))
            {
                return Classify(new IPAddress(ipv6[12..16]));
            }
        }

        if ((ipv6[0] & 0xFE) == 0xFC)
        {
            return NetworkAddressDisposition.Private;
        }

        if (ipv6[0] == 0x20
            && ipv6[1] == 0x01
            && ipv6[2] == 0x0D
            && ipv6[3] == 0xB8)
        {
            return NetworkAddressDisposition.Forbidden;
        }

        if ((ipv6[0] == 0x20
                && ipv6[1] == 0x01
                && (ipv6[2] & 0xFE) == 0)
            || (ipv6[0] == 0x20 && ipv6[1] == 0x02))
        {
            return NetworkAddressDisposition.Forbidden;
        }

        return NetworkAddressDisposition.Public;
    }

    public static bool IsReservedHostName(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        string normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized == "localhost"
            || normalized.EndsWith(".localhost", StringComparison.Ordinal)
            || normalized.EndsWith(".local", StringComparison.Ordinal)
            || normalized.EndsWith(".internal", StringComparison.Ordinal)
            || normalized == "home.arpa"
            || normalized.EndsWith(".home.arpa", StringComparison.Ordinal);
    }
}
