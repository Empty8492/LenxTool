using System.Globalization;
using System.Net;

namespace LenxTool.Infrastructure.Exports;

public sealed record QBittorrentExportTarget(
    string TargetId,
    Uri Endpoint,
    string Category,
    int CredentialVersion = 0)
{
    public const string DefaultTargetId = "default";
    public const string SettingsKey = "integration.qbittorrent.target.v1";

    public string CreateQueueTargetId()
    {
        QBittorrentExportTarget normalized = Normalize(this);
        return IntegrationExportTargetIdentity.Create(
            normalized.TargetId,
            normalized.Endpoint.AbsoluteUri,
            normalized.Category);
    }

    internal bool MatchesQueueTargetId(string? value) =>
        string.Equals(CreateQueueTargetId(), value, StringComparison.Ordinal);

    internal static bool IsSupportedQueueTargetId(string? value) =>
        IntegrationExportTargetIdentity.IsSupported(value, DefaultTargetId);

    public static QBittorrentExportTarget Normalize(
        QBittorrentExportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string category = target.Category?.Trim() ?? string.Empty;
        if (!string.Equals(target.TargetId, DefaultTargetId, StringComparison.Ordinal)
            || category.Length is < 1 or > 128
            || category.Any(char.IsControl)
            || !string.Equals(category, target.Category, StringComparison.Ordinal))
        {
            throw new ArgumentException("qBittorrent 目标或分类无效。", nameof(target));
        }
        if (target.CredentialVersion is not (0 or 1))
        {
            throw new ArgumentException("qBittorrent 凭据代际无效。", nameof(target));
        }
        return target with
        {
            Endpoint = NormalizeEndpoint(target.Endpoint),
            Category = category
        };
    }

    private static Uri NormalizeEndpoint(Uri? value)
    {
        if (value is null
            || !value.IsAbsoluteUri
            || value.AbsoluteUri.Length > 2048
            || !string.IsNullOrEmpty(value.UserInfo)
            || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment)
            || value.AbsolutePath != "/"
            || IPAddress.TryParse(value.IdnHost, out _))
        {
            throw new ArgumentException("qBittorrent 实例地址无效。", nameof(value));
        }
        string host = new IdnMapping()
            .GetAscii(value.IdnHost.TrimEnd('.'))
            .ToLowerInvariant();
        bool loopbackHttp = value.Scheme == Uri.UriSchemeHttp
            && string.Equals(host, "localhost", StringComparison.Ordinal)
            && value.Port is >= 1 and <= 65535;
        bool https = value.Scheme == Uri.UriSchemeHttps
            && host.Contains('.', StringComparison.Ordinal)
            && Uri.CheckHostName(host) == UriHostNameType.Dns
            && !host.EndsWith(".local", StringComparison.Ordinal);
        if (!loopbackHttp && !https)
        {
            throw new ArgumentException(
                "qBittorrent 只允许 HTTPS 或精确 localhost HTTP 端口。",
                nameof(value));
        }
        return new UriBuilder(value)
        {
            Host = host,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
    }
}
