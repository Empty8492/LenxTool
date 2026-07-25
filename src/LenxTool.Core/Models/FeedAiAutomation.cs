namespace LenxTool.Core.Models;

public enum FeedAiAutomationTaskType
{
    Summary,
    Translation
}

public enum FeedAiAutomationJobOutcome
{
    Succeeded,
    Skipped,
    Superseded
}

public sealed record FeedAiAutomationJob(
    string Id,
    string FeedId,
    string EntryId,
    string ContentHash,
    FeedAiAutomationTaskType TaskType,
    string TargetLanguage,
    int AttemptCount,
    string LeaseToken);

public sealed record FeedAiAutomationOptions(
    int BatchSize,
    int MaximumConcurrency,
    TimeSpan LeaseDuration,
    TimeSpan InitialDelay,
    TimeSpan PollInterval,
    TimeSpan BaseRetryDelay,
    TimeSpan MaximumRetryDelay)
{
    public static FeedAiAutomationOptions Default { get; } = new(
        20,
        4,
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromDays(1));
}
