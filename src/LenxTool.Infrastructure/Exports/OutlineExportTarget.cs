using System.Globalization;
using System.Net;

namespace LenxTool.Infrastructure.Exports;

public sealed record OutlineExportTarget(
    string TargetId,
    Uri Endpoint,
    Guid CollectionId,
    int CredentialVersion = 0)
{
    public const string DefaultTargetId = "default";
    public const string SettingsKey = "integration.outline.target.v1";

    public string CreateQueueTargetId()
    {
        OutlineExportTarget normalized = Normalize(this);
        return IntegrationExportTargetIdentity.Create(
            normalized.TargetId,
            normalized.Endpoint.AbsoluteUri,
            normalized.CollectionId.ToString("D"));
    }

    internal bool MatchesQueueTargetId(string? value) =>
        string.Equals(
            CreateQueueTargetId(),
            value,
            StringComparison.Ordinal);

    internal static bool IsSupportedQueueTargetId(string? value) =>
        IntegrationExportTargetIdentity.IsSupported(
            value,
            DefaultTargetId);

    public static OutlineExportTarget Normalize(
        OutlineExportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(
                target.TargetId,
                DefaultTargetId,
                StringComparison.Ordinal)
            || target.CollectionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Outline 目标或 collection ID 无效。",
                nameof(target));
        }
        if (target.CredentialVersion is not (0 or 1))
        {
            throw new ArgumentException(
                "Outline 凭据代际无效。",
                nameof(target));
        }
        return target with
        {
            Endpoint = IntegrationTargetEndpointValidator.NormalizeHttps(
                target.Endpoint)
        };
    }
}

internal static class IntegrationTargetEndpointValidator
{
    public static Uri NormalizeHttps(Uri? value)
    {
        if (value is null
            || !value.IsAbsoluteUri
            || value.AbsoluteUri.Length > 2048
            || value.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(value.UserInfo)
            || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment)
            || value.AbsolutePath != "/"
            || IPAddress.TryParse(value.IdnHost, out _))
        {
            throw new ArgumentException(
                "集成目标必须是无凭据、无查询和无路径的 HTTPS 根地址。",
                nameof(value));
        }
        string host;
        try
        {
            host = new IdnMapping()
                .GetAscii(value.IdnHost.TrimEnd('.'))
                .ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "集成目标 DNS 名称无效。",
                nameof(value),
                exception);
        }
        if (!host.Contains('.', StringComparison.Ordinal)
            || Uri.CheckHostName(host) != UriHostNameType.Dns
            || string.Equals(host, "localhost", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "集成目标必须使用完整精确 DNS 名称。",
                nameof(value));
        }
        var builder = new UriBuilder(value)
        {
            Host = host,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    public static Uri NormalizeHttpsEndpoint(Uri? value)
    {
        if (value is null
            || !value.IsAbsoluteUri
            || value.AbsoluteUri.Length > 2048
            || value.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(value.UserInfo)
            || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment)
            || IPAddress.TryParse(value.IdnHost, out _))
        {
            throw new ArgumentException(
                "集成目标必须是无凭据、无查询的 HTTPS 地址。",
                nameof(value));
        }
        string host;
        try
        {
            host = new IdnMapping()
                .GetAscii(value.IdnHost.TrimEnd('.'))
                .ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "集成目标 DNS 名称无效。",
                nameof(value),
                exception);
        }
        if (!host.Contains('.', StringComparison.Ordinal)
            || Uri.CheckHostName(host) != UriHostNameType.Dns
            || string.Equals(host, "localhost", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "集成目标必须使用完整精确 DNS 名称。",
                nameof(value));
        }
        var builder = new UriBuilder(value)
        {
            Host = host,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }
}
