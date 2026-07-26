namespace LenxTool.Core.Models;

public sealed record FeedAutomationEntryContext(
    string EntryId,
    string FeedId,
    string? CategoryId,
    string Title,
    string? Author,
    string Content,
    string? Language,
    DateTimeOffset? PublishedAt,
    bool HasAudio,
    bool HasVideo);

public enum FeedAutomationRuleEvaluationOutcome
{
    Disabled,
    Matched,
    NotMatched
}

public enum FeedAutomationActionDisposition
{
    Planned,
    Suppressed
}

public enum FeedAutomationActionSuppressionReason
{
    None,
    DuplicateSingleton,
    DuplicateTag
}

public sealed record FeedAutomationRuleEvaluation(
    string RuleId,
    int RuleVersion,
    FeedAutomationRuleEvaluationOutcome Outcome);

public sealed record FeedAutomationActionDecision(
    string RuleId,
    int RuleVersion,
    int RulePriority,
    int RuleConflictOrder,
    FeedAutomationActionType Type,
    int ActionOrder,
    string? Value,
    FeedAutomationActionDisposition Disposition,
    FeedAutomationActionSuppressionReason SuppressionReason,
    string? WinningRuleId,
    int? WinningRuleVersion,
    int? WinningActionOrder);

public sealed record FeedAutomationPlan(
    string EntryId,
    IReadOnlyList<FeedAutomationRuleEvaluation> RuleEvaluations,
    IReadOnlyList<FeedAutomationActionDecision> Actions);

public sealed record FeedAutomationSimulationEntry(
    string EntryId,
    string Title,
    string SourceLabel,
    DateTimeOffset? PublishedAt,
    FeedAutomationRuleEvaluationOutcome Outcome,
    IReadOnlyList<FeedAutomationActionDecision> Actions);

public sealed record FeedAutomationSimulationResult(
    int ExaminedCount,
    int MatchedCount,
    IReadOnlyList<FeedAutomationSimulationEntry> Entries);
