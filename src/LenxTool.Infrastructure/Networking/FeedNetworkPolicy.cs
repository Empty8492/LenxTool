using System.Globalization;
using System.Net;
using System.Net.Sockets;
using LenxTool.Core.Errors;

namespace LenxTool.Infrastructure.Networking;

internal sealed class FeedNetworkPolicy
{
    private readonly IFeedHostResolver _resolver;
    private readonly HashSet<string> _allowedHttpHosts;
    private readonly HashSet<string> _trustedPrivateHosts;

    public FeedNetworkPolicy(IFeedHostResolver resolver, FeedDiscoveryOptions options)
    {
        _resolver = resolver;
        _allowedHttpHosts = NormalizeHosts(options.AllowedHttpHosts);
        _trustedPrivateHosts = NormalizeHosts(options.TrustedPrivateHosts);
    }

    public Uri ParseAndValidate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw InvalidUrl("Feed 地址格式无效。");
        }
        ValidateUri(uri);
        return uri;
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        ValidateUri(uri);
        string host = NormalizeHost(uri.IdnHost);
        IReadOnlyList<IPAddress> resolved;
        bool isLiteralHost = IPAddress.TryParse(host, out IPAddress? literal);
        if (isLiteralHost)
        {
            resolved = [literal!];
        }
        else
        {
            try
            {
                resolved = await _resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException)
            {
                throw new AppException(AppErrorFactory.FromNetwork("Feed 发现"));
            }
            catch (ArgumentException)
            {
                throw new AppException(AppErrorFactory.FromNetwork("Feed 发现"));
            }
        }

        IPAddress[] addresses = resolved.Distinct().ToArray();
        if (addresses.Length == 0)
            throw new AppException(AppErrorFactory.FromNetwork("Feed 发现"));

        bool trustedPrivateHost = _trustedPrivateHosts.Contains(host);
        foreach (IPAddress address in addresses)
        {
            AddressDisposition disposition = Classify(address);
            if (disposition == AddressDisposition.Forbidden
                || ((disposition == AddressDisposition.Private
                        || (disposition == AddressDisposition.SyntheticProxy
                            && (isLiteralHost || uri.Scheme != Uri.UriSchemeHttps)))
                    && !trustedPrivateHost))
            {
                throw UnsafeEndpoint();
            }
        }
        return addresses;
    }

    private void ValidateUri(Uri uri)
    {
        string host = NormalizeHost(uri.IdnHost);
        if (string.IsNullOrWhiteSpace(host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsoluteUri.Length > 2048)
        {
            throw InvalidUrl("Feed 地址不能包含凭据、片段或超长内容。");
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            if (uri.Port != 443) throw InvalidUrl("HTTPS Feed 仅允许 443 端口。");
        }
        else if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (uri.Port != 80 || !_allowedHttpHosts.Contains(host))
                throw InvalidUrl("HTTP Feed 必须按主机显式允许，且仅使用 80 端口。");
        }
        else
        {
            throw InvalidUrl("Feed 地址默认只允许 HTTPS。");
        }

        if (!_trustedPrivateHosts.Contains(host) && IsReservedHostName(host))
            throw UnsafeEndpoint();
    }

    private static AddressDisposition Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return Classify(address.MapToIPv4());
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            byte a = bytes[0];
            byte b = bytes[1];
            if (a == 10
                || (a == 172 && b is >= 16 and <= 31)
                || (a == 192 && b == 168)
                || (a == 100 && b is >= 64 and <= 127))
            {
                return AddressDisposition.Private;
            }
            if (a == 0
                || a == 127
                || a >= 224
                || (a == 169 && b == 254)
                || (a == 192 && b == 0)
                || (a == 192 && b == 2)
                || (a == 192 && b == 88 && bytes[2] == 99)
                || (a == 198 && b == 51 && bytes[2] == 100)
                || (a == 203 && b == 0 && bytes[2] == 113))
            {
                return AddressDisposition.Forbidden;
            }
            if (a == 198 && b is 18 or 19)
            {
                // Clash/sing-box Fake-IP DNS uses 198.18.0.0/15 for public host names.
                // Keep direct literal access blocked while allowing TLS/SNI-bound
                // public HTTPS hosts to traverse the local synthetic proxy mapping.
                return AddressDisposition.SyntheticProxy;
            }
            return AddressDisposition.Public;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6Loopback)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return AddressDisposition.Forbidden;
        }

        byte[] ipv6 = address.GetAddressBytes();
        if (ipv6[..12].All(value => value == 0))
            return AddressDisposition.Forbidden;
        if (ipv6[0] == 0x00 && ipv6[1] == 0x64 && ipv6[2] == 0xFF && ipv6[3] == 0x9B)
        {
            if (ipv6[4] == 0x00 && ipv6[5] == 0x01)
                return AddressDisposition.Forbidden;
            if (ipv6[4..12].All(value => value == 0))
                return Classify(new IPAddress(ipv6[12..16]));
        }
        if ((ipv6[0] & 0xFE) == 0xFC) return AddressDisposition.Private;
        if (ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x0D && ipv6[3] == 0xB8)
            return AddressDisposition.Forbidden;
        if ((ipv6[0] == 0x20 && ipv6[1] == 0x01 && (ipv6[2] & 0xFE) == 0)
            || (ipv6[0] == 0x20 && ipv6[1] == 0x02))
            return AddressDisposition.Forbidden;
        return AddressDisposition.Public;
    }

    private static bool IsReservedHostName(string host) =>
        host == "localhost"
        || host.EndsWith(".localhost", StringComparison.Ordinal)
        || host.EndsWith(".local", StringComparison.Ordinal)
        || host.EndsWith(".internal", StringComparison.Ordinal)
        || host == "home.arpa"
        || host.EndsWith(".home.arpa", StringComparison.Ordinal);

    private static HashSet<string> NormalizeHosts(IEnumerable<string> hosts) =>
        hosts.Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(NormalizeHost)
            .ToHashSet(StringComparer.Ordinal);

    private static string NormalizeHost(string host)
    {
        string normalized = host.Trim().TrimEnd('.');
        return IPAddress.TryParse(normalized, out IPAddress? address)
            ? address.ToString().ToLowerInvariant()
            : new IdnMapping().GetAscii(normalized).ToLowerInvariant();
    }

    private static AppException InvalidUrl(string detail) => new(new(
        AppErrorCode.InvalidRequest,
        "Feed 地址不安全",
        detail,
        "请使用公网 HTTPS Feed；HTTP 或内网主机必须由部署方显式加入可信策略。",
        Provider: "Feed 发现"));

    private static AppException UnsafeEndpoint() => new(new(
        AppErrorCode.AccessDenied,
        "Feed 网络目标已被阻止",
        "该地址解析到本机、私网、链路本地或保留网络。",
        "请改用公网 HTTPS 地址；内网 Feed 只能由部署方按主机显式信任。",
        Provider: "Feed 发现"));

    private enum AddressDisposition
    {
        Public,
        Private,
        SyntheticProxy,
        Forbidden
    }
}
