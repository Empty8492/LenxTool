using System.Globalization;
using System.Net;
using LenxTool.Core.Models;

namespace LenxTool.Core.Exports;

/// <summary>
/// 统一收紧共享集成策略：只接受精确 DNS 主机名，避免通配符、URL 和内网地址穿透白名单。
/// </summary>
public static class EntryIntegrationPolicyValidator
{
    public const int MaximumAllowedHosts = 32;

    private static readonly string[] ReservedSuffixes =
    [
        ".internal",
        ".invalid",
        ".lan",
        ".local",
        ".localhost"
    ];

    public static EntryIntegrationPolicy ValidateAndNormalize(
        EntryIntegrationPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureDefinedKind(input.Kind);
        ArgumentNullException.ThrowIfNull(input.AllowedHosts);
        if (!RequiresAllowedHosts(input.Kind)
            && input.AllowedHosts.Count != 0)
        {
            // 本机集成目标只属于桌面设置；禁止把 Vault、loopback 或任意
            // 替代主机上传到共享 D1，以免泄露本机信息或形成伪白名单。
            throw new ArgumentException(
                "本机集成的共享策略不能配置目标主机。",
                nameof(input));
        }
        if (input.AllowedHosts.Count > MaximumAllowedHosts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                $"单项集成最多允许 {MaximumAllowedHosts} 个目标主机。");
        }

        string[] hosts = input.AllowedHosts
            .Select(NormalizeExactHost)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (input.IsEnabled
            && RequiresAllowedHosts(input.Kind)
            && hosts.Length == 0)
        {
            throw new ArgumentException(
                "启用外部集成前必须配置至少一个精确目标主机。",
                nameof(input));
        }

        return new(
            input.Kind,
            input.IsEnabled,
            Array.AsReadOnly(hosts));
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
        return Array.AsReadOnly(result);
    }

    /// <summary>
    /// Obsidian 与 Eagle 都只使用用户显式授权的本机目标；
    /// 其他集成仍必须由精确 DNS 白名单约束。
    /// </summary>
    private static bool RequiresAllowedHosts(
        EntryIntegrationKind kind) =>
        kind is not (
            EntryIntegrationKind.Obsidian
            or EntryIntegrationKind.Eagle);

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

    private static void EnsureDefinedKind(EntryIntegrationKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
