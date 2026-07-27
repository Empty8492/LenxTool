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
    DateTimeOffset FetchedAt,
    bool HasFullContent = false);

public sealed record ParsedFeedDocument(
    string Title,
    string? SiteUrl,
    FeedDocumentKind Kind,
    IReadOnlyList<FeedEntry> Entries);

public enum FeedEntryReadFilter
{
    All,
    Unread,
    Read
}

public sealed record FeedEntryQuery(
    string? SearchText,
    string? FeedId,
    string? CategoryId,
    DateTimeOffset? PublishedFrom,
    DateTimeOffset? PublishedBefore,
    FeedEntryReadFilter ReadFilter,
    int Offset,
    int Limit,
    bool ActiveOnly = false,
    bool FavoritesOnly = false,
    string? TagId = null,
    string LocalProfile = "default",
    bool IncludeHidden = false,
    EntryViewKind? ViewKind = null);

public sealed record FeedEntryPage(
    IReadOnlyList<FeedEntry> Items,
    int Offset,
    bool HasMore,
    int? NextOffset = null);
