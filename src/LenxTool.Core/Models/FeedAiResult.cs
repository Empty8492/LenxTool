namespace LenxTool.Core.Models;

public enum FeedAiTaskType
{
    Summary,
    Translation
}

public sealed record FeedAiCacheKey(
    string EntryId,
    string ContentHash,
    FeedAiTaskType TaskType,
    string TargetLanguage,
    string Model,
    string PromptVersion);

public sealed record FeedAiResult(
    string Id,
    FeedAiCacheKey CacheKey,
    string Title,
    string Content,
    int RequestCount,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    long DurationMilliseconds,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
