using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed record OutlineCapability(string Version);

public sealed record OutlineDocument(
    Guid Id,
    Guid CollectionId,
    string Title,
    string Text,
    bool Publish = false);

public sealed record OutlineDocumentResult(Guid Id, Uri Url);

public enum OutlineApiFailure
{
    Unauthorized = 1,
    Rejected = 2,
    RateLimited = 3,
    Unavailable = 4,
    UnknownWriteOutcome = 5,
    BlockedEndpoint = 6,
    Cancelled = 7,
    Conflict = 8
}

public sealed class OutlineApiException(
    OutlineApiFailure failure,
    bool isRetryable,
    TimeSpan? retryAfter = null)
    : Exception("Outline 请求失败。")
{
    public OutlineApiFailure Failure { get; } = failure;
    public bool IsRetryable { get; } = isRetryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IOutlineApiClient
{
    Task<OutlineCapability> ProbeAsync(
        EntryIntegrationProbeContext context,
        string token,
        CancellationToken cancellationToken);

    Task<OutlineDocumentResult> UpsertAsync(
        EntryIntegrationProbeContext context,
        string token,
        OutlineDocument document,
        CancellationToken cancellationToken);
}
