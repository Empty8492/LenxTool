namespace LenxTool.Core.Models;

/// <summary>
/// P2 阶段支持的外部集成类型。枚举值是云端策略协议的一部分，禁止重排或复用。
/// </summary>
public enum EntryIntegrationKind
{
    Obsidian = 1,
    Eagle = 2,
    Zotero = 3,
    Readwise = 4,
    Cubox = 5,
    Readeck = 6,
    Outline = 7,
    QBittorrent = 8,
    Webhook = 9
}

/// <summary>
/// 管理员提交的单项集成策略；凭据永远不属于共享策略模型。
/// </summary>
public sealed record EntryIntegrationPolicyInput(
    EntryIntegrationKind Kind,
    bool IsEnabled,
    IReadOnlyList<string> AllowedHosts)
{
    /// <summary>
    /// 管理员明确批准、只允许 HTTPS 访问的私网 DNS 与端口组合。
    /// </summary>
    public IReadOnlyList<EntryIntegrationPrivateEndpoint>
        TrustedPrivateEndpoints
    { get; init; } =
        Array.Empty<EntryIntegrationPrivateEndpoint>();

    /// <summary>
    /// 提供商内的精确资源白名单；当前仅用于 Outline collection ID
    /// 与 qBittorrent category。
    /// </summary>
    public IReadOnlyList<string> AllowedResources { get; init; } =
        Array.Empty<string>();

    /// <summary>
    /// 仅 qBittorrent 可使用的本机 localhost HTTP 端口白名单。
    /// </summary>
    public IReadOnlyList<int> AllowedLoopbackHttpPorts { get; init; } =
        Array.Empty<int>();
}

/// <summary>
/// 共享策略中的私网目标只保存精确 DNS 与端口，不保存 URL、路径或凭据。
/// </summary>
public sealed record EntryIntegrationPrivateEndpoint(
    string Host,
    int Port);

/// <summary>
/// 已规范化的共享集成策略。
/// </summary>
public sealed record EntryIntegrationPolicy(
    EntryIntegrationKind Kind,
    bool IsEnabled,
    IReadOnlyList<string> AllowedHosts)
{
    public IReadOnlyList<EntryIntegrationPrivateEndpoint>
        TrustedPrivateEndpoints
    { get; init; } =
        Array.Empty<EntryIntegrationPrivateEndpoint>();

    public IReadOnlyList<string> AllowedResources { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<int> AllowedLoopbackHttpPorts { get; init; } =
        Array.Empty<int>();
}

public enum EntryIntegrationPolicyScope
{
    Active = 1,
    All = 2
}

/// <summary>
/// 带乐观并发版本的策略快照。
/// </summary>
public sealed record EntryIntegrationPolicySnapshot(
    long Version,
    IReadOnlyList<EntryIntegrationPolicy> Policies,
    EntryIntegrationPolicyScope Scope =
        EntryIntegrationPolicyScope.Active,
    DateTimeOffset? GeneratedAt = null)
{
    /// <summary>
    /// 共享策略表示版本；本地构造默认使用当前 v2，旧 Worker 映射会显式降为 v1。
    /// </summary>
    public int PolicySchemaVersion { get; init; } = 2;
}

/// <summary>
/// 管理员替换策略集合后的安全结果，不回传第三方响应正文。
/// </summary>
public sealed record EntryIntegrationPolicyMutationResult(
    long Version,
    IReadOnlyList<EntryIntegrationPolicy> Policies,
    bool IsReplay)
{
    public int PolicySchemaVersion { get; init; } = 2;
}
