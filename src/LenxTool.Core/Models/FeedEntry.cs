namespace LenxTool.Core.Models;

public sealed record FeedEnclosure(
    string Url,
    string? MediaType,
    long? Length,
    string? Title);

public sealed record FeedEntry(
    string Id,
    string FeedId,
    string ExternalId,
    string? NormalizedUrl,
    string Title,
    string? Author,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? UpdatedAt,
    string Summary,
    string SanitizedContent,
    IReadOnlyList<string> Categories,
    IReadOnlyList<FeedEnclosure> Enclosures,
    string ContentHash,
    DateTimeOffset FetchedAt);

public sealed record ParsedFeedDocument(
    string Title,
    string? SiteUrl,
    FeedDocumentKind Kind,
    IReadOnlyList<FeedEntry> Entries);
