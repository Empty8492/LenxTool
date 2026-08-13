using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed record ReadeckBookmark(
    string StableLabel,
    Uri SourceUrl,
    string Title,
    IReadOnlyList<string> Labels,
    bool IsArchived);

public sealed record ReadeckBookmarkResult(string Id, Uri Url);

public enum ReadeckApiFailure
{
    Unauthorized = 1,
    Rejected = 2,
    RateLimited = 3,
    Unavailable = 4,
    UnknownWriteOutcome = 5,
    BlockedEndpoint = 6,
    Conflict = 7,
    Cancelled = 8
}

public sealed class ReadeckApiException(
    ReadeckApiFailure failure,
    TimeSpan? retryAfter = null)
    : Exception("Readeck 请求失败。")
{
    public ReadeckApiFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IReadeckApiClient
{
    Task ProbeAsync(
        EntryIntegrationProbeContext context,
        string token,
        CancellationToken cancellationToken);

    Task<ReadeckBookmarkResult> UpsertAsync(
        EntryIntegrationProbeContext context,
        string token,
        ReadeckBookmark bookmark,
        CancellationToken cancellationToken);
}
