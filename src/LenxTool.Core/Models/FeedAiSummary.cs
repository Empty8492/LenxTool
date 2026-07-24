using LenxTool.Core.Errors;

namespace LenxTool.Core.Models;

public sealed record FeedAiSummaryInput(
    string EntryId,
    string ContentHash,
    string Title,
    string Content);

public sealed record FeedAiSummaryBatchItem(
    string EntryId,
    FeedAiResult? Result,
    AppError? Error);

public sealed record FeedAiSummaryOptions(
    string Model,
    string PromptVersion,
    int MaximumSourceCharacters,
    int MaximumResponseBytes,
    int MaximumOutputTokens,
    int MaximumSummaryCharacters,
    int MaximumBatchSize,
    int MaximumConcurrency)
{
    public static FeedAiSummaryOptions Default { get; } = new(
        "deepseek-v4-flash",
        "feed-summary-v1",
        16_000,
        2_000_000,
        1200,
        8_000,
        20,
        2);
}
