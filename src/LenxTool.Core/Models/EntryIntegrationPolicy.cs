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
    IReadOnlyList<string> AllowedHosts);

/// <summary>
/// 已规范化的共享集成策略。
/// </summary>
public sealed record EntryIntegrationPolicy(
    EntryIntegrationKind Kind,
    bool IsEnabled,
    IReadOnlyList<string> AllowedHosts);

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
    DateTimeOffset? GeneratedAt = null);

/// <summary>
/// 管理员替换策略集合后的安全结果，不回传第三方响应正文。
/// </summary>
public sealed record EntryIntegrationPolicyMutationResult(
    long Version,
    IReadOnlyList<EntryIntegrationPolicy> Policies,
    bool IsReplay);
