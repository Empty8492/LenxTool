namespace LenxTool.Core.Models;

public enum FeedAutomationMatchMode
{
    All,
    Any
}

public enum FeedAutomationField
{
    Feed,
    Category,
    Title,
    Author,
    Content,
    Language,
    PublishedAt,
    HasAudio,
    HasVideo
}

public enum FeedAutomationOperator
{
    Equals,
    Contains,
    Regex,
    Before,
    After,
    Exists
}

public enum FeedAutomationActionType
{
    AddTag,
    Hide,
    MarkRead,
    GenerateSummary,
    Translate,
    SendToMedia,
    Notify
}

public sealed record FeedAutomationCondition(
    FeedAutomationField Field,
    FeedAutomationOperator Operator,
    string? Value);

public sealed record FeedAutomationAction(
    FeedAutomationActionType Type,
    int Order,
    string? Value);

public sealed record FeedAutomationRule(
    string Id,
    int Version,
    string Name,
    int Priority,
    int ConflictOrder,
    bool IsEnabled,
    FeedAutomationMatchMode MatchMode,
    IReadOnlyList<FeedAutomationCondition> Conditions,
    IReadOnlyList<FeedAutomationAction> Actions);

public sealed record FeedAutomationRuleSnapshot(
    long RuleSetVersion,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<FeedAutomationRule> Rules);
