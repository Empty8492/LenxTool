namespace LenxTool.Core.Models;

public sealed record FeedMediaDelivery(
    string EntryId,
    string FeedId,
    string EntryTitle,
    string SourceUrl,
    string? SourceTitle,
    string MediaType,
    long? SourceLength,
    string MediaJobId,
    DateTimeOffset CreatedAt);

public sealed record FeedMediaDeliveryRegistration(
    FeedMediaDelivery Delivery,
    MediaJob Job,
    bool Created);
