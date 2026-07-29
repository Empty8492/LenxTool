using System.Net;

namespace LenxTool.Core.Models;

/// <summary>
/// 本机的非敏感集成目标；凭据由独立的 DPAPI 存储按 TargetId 隔离。
/// </summary>
public sealed record EntryIntegrationTarget(
    string TargetId,
    EntryIntegrationKind Kind,
    Uri Endpoint);

public enum EntryIntegrationHealthStatus
{
    Healthy = 1,
    PolicyDisabled = 2,
    BlockedEndpoint = 3,
    CredentialsMissing = 4,
    AdapterUnavailable = 5,
    Unauthorized = 6,
    RateLimited = 7,
    TimedOut = 8,
    Unavailable = 9
}

/// <summary>
/// 面向界面的封闭健康结果，刻意不承载异常、响应正文、请求头或完整 URL。
/// </summary>
public sealed record EntryIntegrationHealthResult(
    EntryIntegrationHealthStatus Status,
    DateTimeOffset CheckedAt,
    TimeSpan? RetryAfter = null);

/// <summary>
/// 提供给具体适配器的、已经完成主机与 DNS 校验的连接上下文。
/// </summary>
public sealed record EntryIntegrationProbeContext(
    Uri Endpoint,
    IReadOnlyList<IPAddress> PinnedAddresses);

/// <summary>
/// 适配器只能返回封闭状态；第三方正文和异常文本不得跨越此边界。
/// </summary>
public sealed record EntryIntegrationProbeResult(
    EntryIntegrationHealthStatus Status,
    TimeSpan? RetryAfter = null)
{
    public static EntryIntegrationProbeResult Healthy() =>
        new(EntryIntegrationHealthStatus.Healthy);
}

public sealed record EntryIntegrationHealthOptions(
    TimeSpan Timeout,
    TimeSpan Cooldown,
    int MaximumConcurrency)
{
    public static EntryIntegrationHealthOptions Default { get; } =
        new(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(30),
            MaximumConcurrency: 2);
}
