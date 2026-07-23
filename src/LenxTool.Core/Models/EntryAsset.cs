namespace LenxTool.Core.Models;

public sealed record EntryAsset(
    string EntryId,
    string SourceUrl,
    string ContentHash,
    string MimeType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt);
