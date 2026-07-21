using LenxTool.Core.Errors;

namespace LenxTool.Core.Models;

public enum MediaJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum TranscriptionEngine
{
    Groq,
    SharedGroq,
    LocalWhisper,
    ImportedSrt
}

public sealed record MediaJob(
    string Id,
    string Kind,
    string InputPath,
    string? OutputPath,
    MediaJobStatus Status,
    double Progress,
    TranscriptionEngine Engine,
    string? Model,
    double SharedUsageSeconds,
    int AiRequestCount,
    AppError? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string? TranslationProvider { get; init; }

    public string? TranslationTargetLanguage { get; init; }

    public int TranslationNextSegmentIndex { get; init; }

    public int TranslationPromptTokens { get; init; }

    public int TranslationCompletionTokens { get; init; }

    public int TranslationTotalTokens { get; init; }
}
