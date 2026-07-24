namespace LenxTool.Core.Models;

public sealed record FeedFullTextWorkItem(
    string EntryId,
    string FeedId,
    string Url,
    string Host,
    int AttemptCount,
    string LeaseId);

public sealed record FeedFullTextContent(
    string EntryId,
    ArticleContentResult Article,
    string ContentHash,
    DateTimeOffset ExtractedAt);
