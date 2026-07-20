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
    LocalWhisper
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
    DateTimeOffset UpdatedAt);
