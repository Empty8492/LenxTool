namespace LenxTool.Core.Models;

public sealed record AiReport(
    string Id,
    string EntityType,
    string? EntityId,
    string ReportType,
    string Title,
    string Content,
    string Model,
    int RequestCount,
    int TokenUsage,
    DateTimeOffset CreatedAt);
