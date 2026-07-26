namespace LenxTool.Core.Models;

public enum FeedAutomationActionRunStatus
{
    Pending,
    Running,
    Retry,
    Succeeded,
    Failed,
    Suppressed
}

public enum FeedAutomationActionRunOutcome
{
    Succeeded,
    Failed
}

public enum FeedAutomationLocalActionResult
{
    Completed,
    EntryMissing
}

public enum FeedAutomationAiActionResult
{
    Completed,
    EntryMissing,
    FeedUnavailable
}

public enum FeedAutomationMediaActionResult
{
    Completed,
    EntryMissing,
    FeedUnavailable,
    NoSupportedMedia
}

public sealed record FeedAutomationRuleRun(
    string EntryId,
    string RuleId,
    int RuleVersion,
    FeedAutomationRuleEvaluationOutcome Outcome,
    int PlanOrder,
    DateTimeOffset EvaluatedAt);

public sealed record FeedAutomationActionRun(
    string IdempotencyKey,
    string EntryId,
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
    int? WinningActionOrder,
    FeedAutomationActionRunStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    string? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FeedAutomationActionLease(
    string IdempotencyKey,
    string EntryId,
    string RuleId,
    int RuleVersion,
    int RulePriority,
    int RuleConflictOrder,
    FeedAutomationActionType Type,
    int ActionOrder,
    string? Value,
    int AttemptCount,
    string LeaseToken);

public sealed record FeedAutomationRunSnapshot(
    IReadOnlyList<FeedAutomationRuleRun> RuleRuns,
    IReadOnlyList<FeedAutomationActionRun> ActionRuns);

public sealed record FeedAutomationStageResult(
    int RuleRunsCreated,
    int ActionRunsCreated);

public sealed record FeedAutomationPlanningResult(
    long RuleSetVersion,
    int EntriesEvaluated,
    int RuleRunsCreated,
    int ActionRunsCreated);

public sealed record FeedAutomationActionProcessorOptions(
    int BatchSize,
    int MaximumConcurrency,
    int MaximumAttempts,
    TimeSpan LeaseDuration,
    TimeSpan InitialDelay,
    TimeSpan PollInterval,
    TimeSpan BaseRetryDelay,
    TimeSpan MaximumRetryDelay)
{
    public static FeedAutomationActionProcessorOptions Default { get; } = new(
        20,
        4,
        5,
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromHours(1));
}
