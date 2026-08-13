using System.Globalization;
using System.Net;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// 把策略匹配、DNS 分类与地址 pin 集中在同一实现，避免健康检查安全而真实写入绕过。
/// </summary>
internal sealed class EntryIntegrationEndpointAuthorizer(
    IFeedHostResolver resolver)
    : IEntryIntegrationEndpointAuthorizer
{
    public async Task<EntryIntegrationProbeContext?> AuthorizeAsync(
        EntryIntegrationTarget target,
        EntryIntegrationPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Kind != policy.Kind
            || !policy.IsEnabled
            || !TryValidateEndpoint(
                target,
                policy,
                out Uri endpoint,
                out EndpointAccess access))
        {
            return null;
        }

        IReadOnlyList<IPAddress> resolved;
        try
        {
            resolved = await resolver.ResolveAsync(
                    NormalizeHost(endpoint.IdnHost),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        IPAddress[] addresses = resolved.Distinct().ToArray();
        bool approved = access switch
        {
            EndpointAccess.PublicHttps =>
                addresses.All(address =>
                    NetworkTargetClassifier.Classify(address)
                        is NetworkAddressDisposition.Public
                        or NetworkAddressDisposition.SyntheticProxy),
            EndpointAccess.TrustedPrivateHttps =>
                addresses.All(address =>
                    NetworkTargetClassifier.Classify(address)
                        == NetworkAddressDisposition.Private),
            EndpointAccess.QBittorrentLoopbackHttp =>
                addresses.All(IPAddress.IsLoopback),
            _ => false
        };
        return addresses.Length == 0 || !approved
            ? null
            : new EntryIntegrationProbeContext(
                endpoint,
                Array.AsReadOnly(addresses));
    }

    private static bool TryValidateEndpoint(
        EntryIntegrationTarget target,
        EntryIntegrationPolicy policy,
        out Uri endpoint,
        out EndpointAccess access)
    {
        endpoint = null!;
        access = default;
        Uri? value = target.Endpoint;
        if (value is null
            || !value.IsAbsoluteUri
            || value.AbsoluteUri.Length > 2048
            || !string.IsNullOrEmpty(value.UserInfo)
            || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment)
            || IPAddress.TryParse(value.IdnHost, out _))
        {
            return false;
        }
        string host;
        try
        {
            host = NormalizeHost(value.IdnHost);
        }
        catch (ArgumentException)
        {
            return false;
        }

        bool publicHttps = value.Scheme == Uri.UriSchemeHttps
            && value.Port == 443
            && !NetworkTargetClassifier.IsReservedHostName(host)
            && policy.AllowedHosts.Contains(host, StringComparer.Ordinal);
        bool trustedPrivateHttps = value.Scheme == Uri.UriSchemeHttps
            && !IsForbiddenPrivateHost(host)
            && policy.TrustedPrivateEndpoints.Contains(
                new EntryIntegrationPrivateEndpoint(host, value.Port));
        bool qBittorrentLoopbackHttp =
            target.Kind == EntryIntegrationKind.QBittorrent
            && value.Scheme == Uri.UriSchemeHttp
            && string.Equals(host, "localhost", StringComparison.Ordinal)
            && policy.AllowedLoopbackHttpPorts.Contains(value.Port);
        if (!publicHttps
            && !trustedPrivateHttps
            && !qBittorrentLoopbackHttp)
        {
            return false;
        }
        access = publicHttps
            ? EndpointAccess.PublicHttps
            : trustedPrivateHttps
                ? EndpointAccess.TrustedPrivateHttps
                : EndpointAccess.QBittorrentLoopbackHttp;
        endpoint = value;
        return true;
    }

    private static bool IsForbiddenPrivateHost(string host) =>
        string.Equals(host, "localhost", StringComparison.Ordinal)
        || host.EndsWith(".localhost", StringComparison.Ordinal)
        || host.EndsWith(".local", StringComparison.Ordinal)
        || host.EndsWith(".invalid", StringComparison.Ordinal);

    private static string NormalizeHost(string value) =>
        new IdnMapping()
            .GetAscii(value.Trim().TrimEnd('.'))
            .ToLowerInvariant();

    private enum EndpointAccess
    {
        PublicHttps = 1,
        TrustedPrivateHttps = 2,
        QBittorrentLoopbackHttp = 3
    }
}
