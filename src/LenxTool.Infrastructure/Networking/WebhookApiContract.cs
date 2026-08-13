using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed record WebhookEntryPayload(
    string EventId,
    string EntryId,
    string Title,
    Uri? Url,
    string? Author,
    DateTimeOffset? PublishedAt,
    string Summary,
    IReadOnlyList<string> Categories,
    EntryViewKind ViewKind);

public enum WebhookApiFailure
{
    CapabilityMissing = 1,
    Rejected = 2,
    RateLimited = 3,
    Unavailable = 4,
    UnknownWriteOutcome = 5,
    BlockedEndpoint = 6,
    Cancelled = 7
}

public sealed class WebhookApiException(
    WebhookApiFailure failure,
    TimeSpan? retryAfter = null)
    : Exception("Webhook 请求失败。")
{
    public WebhookApiFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IWebhookApiClient
{
    Task ProbeAsync(
        EntryIntegrationProbeContext context,
        CancellationToken cancellationToken);

    Task SendAsync(
        EntryIntegrationProbeContext context,
        string? hmacSecret,
        WebhookEntryPayload payload,
        CancellationToken cancellationToken);
}
