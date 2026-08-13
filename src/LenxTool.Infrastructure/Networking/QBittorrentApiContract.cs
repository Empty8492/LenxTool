using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public abstract record QBittorrentSource(string InfoHash);

public sealed record QBittorrentMagnetSource(
    string Magnet,
    string Hash)
    : QBittorrentSource(Hash);

public sealed record QBittorrentFileSource(
    byte[] Content,
    string Hash)
    : QBittorrentSource(Hash);

public enum QBittorrentApiFailure
{
    Unauthorized = 1,
    Rejected = 2,
    RateLimited = 3,
    Unavailable = 4,
    UnknownWriteOutcome = 5,
    BlockedEndpoint = 6,
    UnsupportedVersion = 7,
    Cancelled = 8,
    Conflict = 9
}

public sealed class QBittorrentApiException(
    QBittorrentApiFailure failure,
    TimeSpan? retryAfter = null)
    : Exception("qBittorrent 请求失败。")
{
    public QBittorrentApiFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IQBittorrentApiClient
{
    Task ProbeAsync(
        EntryIntegrationProbeContext context,
        string apiKey,
        CancellationToken cancellationToken);

    Task AddAsync(
        EntryIntegrationProbeContext context,
        string apiKey,
        QBittorrentSource source,
        string category,
        CancellationToken cancellationToken);
}

public interface ITorrentFileFetcher
{
    Task<QBittorrentFileSource> FetchAsync(
        FeedEnclosure enclosure,
        CancellationToken cancellationToken);
}
