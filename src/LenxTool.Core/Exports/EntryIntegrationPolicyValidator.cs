using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LenxTool.Core.Models;

namespace LenxTool.Core.Exports;

/// <summary>
/// 统一收紧共享集成策略：只接受精确 DNS 主机名，避免通配符、URL 和内网地址穿透白名单。
/// </summary>
public static class EntryIntegrationPolicyValidator
{
    public const int MaximumAllowedHosts = 32;
    public const int MaximumTrustedPrivateEndpoints = 32;
    public const int MaximumAllowedResources = 32;
    public const int MaximumLoopbackHttpPorts = 16;
    public const int MaximumResourceLength = 128;
    public const int MaximumJsonColumnLength = 8 * 1024;
    public const int MaximumPolicySetJsonBytes = 40 * 1024;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            // 与 Worker JSON.stringify 的 Unicode 预算一致；这里只计算规范化 JSON，
            // 不把该选项用于 HTML 或脚本上下文。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

    private static readonly string[] ReservedSuffixes =
    [
        ".internal",
        ".invalid",
        ".lan",
        ".local",
        ".localhost",
        ".home.arpa"
    ];

    private static readonly string[] ForbiddenPrivateSuffixes =
    [
        ".invalid",
        ".local",
        ".localhost"
    ];

    public static EntryIntegrationPolicy ValidateAndNormalize(
        EntryIntegrationPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureDefinedKind(input.Kind);
        ArgumentNullException.ThrowIfNull(input.AllowedHosts);
        ArgumentNullException.ThrowIfNull(input.TrustedPrivateEndpoints);
        ArgumentNullException.ThrowIfNull(input.AllowedResources);
        ArgumentNullException.ThrowIfNull(input.AllowedLoopbackHttpPorts);
        if (IsLocalOnly(input.Kind)
            && (input.AllowedHosts.Count != 0
                || input.TrustedPrivateEndpoints.Count != 0
                || input.AllowedResources.Count != 0
                || input.AllowedLoopbackHttpPorts.Count != 0))
        {
            // 本机集成目标只属于桌面设置；禁止把 Vault、loopback 或任意
            // 替代主机上传到共享 D1，以免泄露本机信息或形成伪白名单。
            throw new ArgumentException(
                "本机集成的共享策略不能配置目标主机或资源。",
                nameof(input));
        }
        if (!SupportsPrivateEndpoints(input.Kind)
            && input.TrustedPrivateEndpoints.Count != 0)
        {
            throw new ArgumentException(
                "该集成类型不能配置受信私网目标。",
                nameof(input));
        }
        if (!SupportsResources(input.Kind)
            && input.AllowedResources.Count != 0)
        {
            throw new ArgumentException(
                "该集成类型不能配置资源白名单。",
                nameof(input));
        }
        if (input.Kind != EntryIntegrationKind.QBittorrent
            && input.AllowedLoopbackHttpPorts.Count != 0)
        {
            throw new ArgumentException(
                "只有 qBittorrent 可以配置本机 HTTP 端口。",
                nameof(input));
        }
        if (input.AllowedHosts.Count > MaximumAllowedHosts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                $"单项集成最多允许 {MaximumAllowedHosts} 个目标主机。");
        }
        if (input.TrustedPrivateEndpoints.Count
                > MaximumTrustedPrivateEndpoints
            || input.AllowedResources.Count > MaximumAllowedResources
            || input.AllowedLoopbackHttpPorts.Count
                > MaximumLoopbackHttpPorts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "集成策略扩展白名单超过支持上限。");
        }

        string[] hosts = input.AllowedHosts
            .Select(NormalizeExactHost)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        EntryIntegrationPrivateEndpoint[] privateEndpoints =
            input.TrustedPrivateEndpoints
                .Select(NormalizePrivateEndpoint)
                .Distinct()
                .OrderBy(endpoint => endpoint.Host, StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.Port)
                .ToArray();
        string[] resources = NormalizeResources(
            input.Kind,
            input.AllowedResources);
        int[] loopbackPorts = input.AllowedLoopbackHttpPorts
            .Select(NormalizePort)
            .Distinct()
            .Order()
            .ToArray();
        EnsureColumnBudget(hosts, "目标主机");
        EnsureColumnBudget(
            privateEndpoints.Select(endpoint => new
            {
                endpoint.Host,
                endpoint.Port
            }),
            "受信私网目标");
        EnsureColumnBudget(resources, "资源白名单");
        EnsureColumnBudget(loopbackPorts, "本机 HTTP 端口");
        if (input.IsEnabled
            && RequiresNetworkTarget(input.Kind)
            && hosts.Length == 0
            && privateEndpoints.Length == 0
            && loopbackPorts.Length == 0)
        {
            throw new ArgumentException(
                "启用外部集成前必须配置至少一个受控网络目标。",
                nameof(input));
        }
        if (input.IsEnabled
            && SupportsResources(input.Kind)
            && resources.Length == 0)
        {
            throw new ArgumentException(
                "启用该集成前必须配置至少一个允许资源。",
                nameof(input));
        }

        return new(
            input.Kind,
            input.IsEnabled,
            Array.AsReadOnly(hosts))
        {
            TrustedPrivateEndpoints =
                Array.AsReadOnly(privateEndpoints),
            AllowedResources = Array.AsReadOnly(resources),
            AllowedLoopbackHttpPorts =
                Array.AsReadOnly(loopbackPorts)
        };
    }

    public static IReadOnlyList<EntryIntegrationPolicy>
        ValidateAndNormalizeSet(
            IEnumerable<EntryIntegrationPolicyInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var kinds = new HashSet<EntryIntegrationKind>();
        var policies = new List<EntryIntegrationPolicy>();
        foreach (EntryIntegrationPolicyInput input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            EnsureDefinedKind(input.Kind);
            if (!kinds.Add(input.Kind))
            {
                throw new ArgumentException(
                    $"集成类型 {input.Kind} 不能重复配置。",
                    nameof(inputs));
            }
            policies.Add(ValidateAndNormalize(input));
        }

        EntryIntegrationPolicy[] result = policies
            .OrderBy(policy => policy.Kind)
            .ToArray();
        var canonicalWirePolicies = result.Select(policy => new
        {
            Kind = ToWireKind(policy.Kind),
            policy.IsEnabled,
            policy.AllowedHosts,
            TrustedPrivateEndpoints =
                policy.TrustedPrivateEndpoints.Select(endpoint => new
                {
                    endpoint.Host,
                    endpoint.Port
                }).ToArray(),
            policy.AllowedResources,
            policy.AllowedLoopbackHttpPorts
        }).ToArray();
        if (Encoding.UTF8.GetByteCount(
                JsonSerializer.Serialize(canonicalWirePolicies, JsonOptions))
            > MaximumPolicySetJsonBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputs),
                "集成策略集合超过安全传输预算。");
        }
        return Array.AsReadOnly(result);
    }

    private static string ToWireKind(EntryIntegrationKind kind) =>
        kind switch
        {
            EntryIntegrationKind.Obsidian => "OBSIDIAN",
            EntryIntegrationKind.Eagle => "EAGLE",
            EntryIntegrationKind.Zotero => "ZOTERO",
            EntryIntegrationKind.Readwise => "READWISE",
            EntryIntegrationKind.Cubox => "CUBOX",
            EntryIntegrationKind.Readeck => "READECK",
            EntryIntegrationKind.Outline => "OUTLINE",
            EntryIntegrationKind.QBittorrent => "QBITTORRENT",
            EntryIntegrationKind.Webhook => "WEBHOOK",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    /// <summary>
    /// Obsidian 与 Eagle 都只使用用户显式授权的本机目标。
    /// </summary>
    private static bool IsLocalOnly(
        EntryIntegrationKind kind) =>
        kind is (
            EntryIntegrationKind.Obsidian
            or EntryIntegrationKind.Eagle);

    private static bool RequiresNetworkTarget(
        EntryIntegrationKind kind) => !IsLocalOnly(kind);

    private static bool SupportsPrivateEndpoints(
        EntryIntegrationKind kind) =>
        kind is (
            EntryIntegrationKind.Readeck
            or EntryIntegrationKind.Outline
            or EntryIntegrationKind.QBittorrent
            or EntryIntegrationKind.Webhook);

    private static bool SupportsResources(
        EntryIntegrationKind kind) =>
        kind is (
            EntryIntegrationKind.Outline
            or EntryIntegrationKind.QBittorrent);

    private static EntryIntegrationPrivateEndpoint
        NormalizePrivateEndpoint(
            EntryIntegrationPrivateEndpoint value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(
            NormalizePrivateDnsHost(value.Host),
            NormalizePort(value.Port));
    }

    private static int NormalizePort(int value)
    {
        if (value is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "目标端口必须位于 1 到 65535 之间。");
        }
        return value;
    }

    private static string[] NormalizeResources(
        EntryIntegrationKind kind,
        IReadOnlyList<string> values)
    {
        if (!SupportsResources(kind)) return [];
        IEnumerable<string> normalized = kind switch
        {
            EntryIntegrationKind.Outline =>
                values.Select(NormalizeOutlineCollectionId),
            EntryIntegrationKind.QBittorrent =>
                values.Select(NormalizeQBittorrentCategory),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return normalized
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeOutlineCollectionId(string value)
    {
        if (!Guid.TryParseExact(value?.Trim(), "D", out Guid id)
            || id == Guid.Empty)
        {
            throw new ArgumentException(
                "Outline collection ID 必须是非空 UUID。",
                nameof(value));
        }
        return id.ToString("D");
    }

    private static string NormalizeQBittorrentCategory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "qBittorrent 分类不能为空。",
                nameof(value));
        }
        string category = value.Trim();
        if (category.Length > MaximumResourceLength
            || category.Any(char.IsControl))
        {
            throw new ArgumentException(
                "qBittorrent 分类格式无效。",
                nameof(value));
        }
        return category;
    }

    private static void EnsureColumnBudget<T>(
        IEnumerable<T> values,
        string label)
    {
        string json = JsonSerializer.Serialize(values, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json)
            > MaximumJsonColumnLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                $"{label}超过共享策略列预算。");
        }
    }

    private static string NormalizeExactHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("目标主机不能为空。", nameof(value));
        }

        string candidate = value.Trim().TrimEnd('.');
        if (candidate.Length == 0
            || candidate.Contains('*', StringComparison.Ordinal)
            || candidate.Contains(
                Uri.SchemeDelimiter,
                StringComparison.Ordinal)
            || candidate.IndexOfAny(
                ['/', '\\', '@', ':', '?', '#']) >= 0
            || IPAddress.TryParse(candidate, out _))
        {
            throw new ArgumentException(
                "目标必须是精确 DNS 主机名，不能包含协议、端口、路径、通配符或 IP 地址。",
                nameof(value));
        }

        string host;
        try
        {
            host = new IdnMapping()
                .GetAscii(candidate)
                .ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "目标主机不是有效的国际化 DNS 名称。",
                nameof(value),
                exception);
        }

        if (host.Length > 253
            || !host.Contains('.', StringComparison.Ordinal)
            || Uri.CheckHostName(host) != UriHostNameType.Dns
            || string.Equals(
                host,
                "localhost",
                StringComparison.Ordinal)
            || string.Equals(host, "home.arpa", StringComparison.Ordinal)
            || ReservedSuffixes.Any(
                suffix => host.EndsWith(
                    suffix,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "目标必须是可公开解析的精确 DNS 主机名。",
                nameof(value));
        }
        return host;
    }

    private static string NormalizePrivateDnsHost(string value)
    {
        string host = NormalizeDnsSyntax(value);
        if (string.Equals(host, "localhost", StringComparison.Ordinal)
            || ForbiddenPrivateSuffixes.Any(
                suffix => host.EndsWith(
                    suffix,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "受信私网目标必须是精确 DNS 名称，不能使用 localhost、.local 或无效保留域。",
                nameof(value));
        }
        return host;
    }

    private static string NormalizeDnsSyntax(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("目标主机不能为空。", nameof(value));
        }
        string candidate = value.Trim().TrimEnd('.');
        if (candidate.Length == 0
            || candidate.Contains('*', StringComparison.Ordinal)
            || candidate.Contains(Uri.SchemeDelimiter, StringComparison.Ordinal)
            || candidate.IndexOfAny(['/', '\\', '@', ':', '?', '#']) >= 0
            || IPAddress.TryParse(candidate, out _))
        {
            throw new ArgumentException(
                "目标必须是精确 DNS 主机名，不能包含协议、端口、路径、通配符或 IP 地址。",
                nameof(value));
        }
        string host;
        try
        {
            host = new IdnMapping()
                .GetAscii(candidate)
                .ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "目标主机不是有效的国际化 DNS 名称。",
                nameof(value),
                exception);
        }
        if (host.Length > 253
            || !host.Contains('.', StringComparison.Ordinal)
            || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            throw new ArgumentException(
                "目标必须是完整的精确 DNS 主机名。",
                nameof(value));
        }
        return host;
    }

    private static void EnsureDefinedKind(EntryIntegrationKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
