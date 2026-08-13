using System.Net;
using System.Net.Sockets;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal enum TorrentFileFetchFailure
{
    InvalidSource = 1,
    AccessDenied = 2,
    RateLimited = 3,
    Unavailable = 4
}

internal sealed class TorrentFileFetchException(
    TorrentFileFetchFailure failure,
    TimeSpan? retryAfter = null)
    : Exception("Torrent enclosure 获取失败。")
{
    public TorrentFileFetchFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal interface ITorrentFileTransport
{
    Task<TorrentFileHttpResponse> GetAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken);
}

internal sealed class TorrentFileHttpResponse(
    HttpResponseMessage message,
    IDisposable? owner = null) : IDisposable
{
    public HttpResponseMessage Message { get; } = message;

    public void Dispose()
    {
        Message.Dispose();
        owner?.Dispose();
    }
}

internal sealed class PinnedTorrentFileTransport : ITorrentFileTransport
{
    public async Task<TorrentFileHttpResponse> GetAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken)
    {
        SocketsHttpHandler handler = PinnedHttpHandlerFactory.Create(
            uri,
            addresses,
            TimeSpan.FromSeconds(5),
            DecompressionMethods.None);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            return new(response, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

/// <summary>
/// 只从已解析为公网的 HTTPS enclosure 下载有界 torrent，并在交给 qBittorrent 前解析 metainfo。
/// </summary>
internal sealed class TorrentFileFetcher : ITorrentFileFetcher
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(24);
    private readonly IFeedHostResolver _resolver;
    private readonly ITorrentFileTransport _transport;

    public TorrentFileFetcher(IFeedHostResolver resolver)
        : this(resolver, new PinnedTorrentFileTransport())
    {
    }

    internal TorrentFileFetcher(
        IFeedHostResolver resolver,
        ITorrentFileTransport transport)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<QBittorrentFileSource> FetchAsync(
        FeedEnclosure enclosure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enclosure);
        Uri uri = ValidateEnclosure(enclosure);
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);

        IReadOnlyList<IPAddress> resolved;
        try
        {
            resolved = await _resolver.ResolveAsync(uri.IdnHost, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is OperationCanceledException
                or SocketException
                or HttpRequestException
                or ArgumentException)
        {
            throw Failure(TorrentFileFetchFailure.Unavailable);
        }

        IPAddress[] addresses = resolved.Distinct().ToArray();
        if (addresses.Length == 0)
        {
            throw Failure(TorrentFileFetchFailure.Unavailable);
        }
        if (addresses.Any(address =>
                NetworkTargetClassifier.Classify(address)
                    is not (NetworkAddressDisposition.Public
                        or NetworkAddressDisposition.SyntheticProxy)))
        {
            throw Failure(TorrentFileFetchFailure.AccessDenied);
        }

        try
        {
            using TorrentFileHttpResponse ownedResponse = await _transport
                .GetAsync(uri, addresses, timeout.Token)
                .ConfigureAwait(false);
            HttpResponseMessage response = ownedResponse.Message;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw Failure(
                    TorrentFileFetchFailure.RateLimited,
                    GetRetryAfter(response));
            }
            if (response.StatusCode == HttpStatusCode.RequestTimeout
                || response.StatusCode >= HttpStatusCode.InternalServerError)
            {
                throw Failure(TorrentFileFetchFailure.Unavailable);
            }
            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentLength > MaximumBytes
                || !string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "application/x-bittorrent",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Failure(TorrentFileFetchFailure.InvalidSource);
            }
            byte[] content = await BoundedHttpContent
                .ReadAsByteArrayAsync(
                    response.Content,
                    MaximumBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            try
            {
                return TorrentMetainfoValidator.Validate(content);
            }
            catch (ArgumentException)
            {
                throw Failure(TorrentFileFetchFailure.InvalidSource);
            }
        }
        catch (TorrentFileFetchException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw Failure(TorrentFileFetchFailure.InvalidSource);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException
                or SocketException
                or HttpRequestException
                or IOException)
        {
            throw Failure(TorrentFileFetchFailure.Unavailable);
        }
    }

    private static Uri ValidateEnclosure(FeedEnclosure enclosure)
    {
        if (!Uri.TryCreate(enclosure.Url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsoluteUri.Length > 2048
            || enclosure.Length > MaximumBytes
            || NetworkTargetClassifier.IsReservedHostName(uri.IdnHost))
        {
            throw Failure(TorrentFileFetchFailure.InvalidSource);
        }
        return uri;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            retryAfter = date - DateTimeOffset.UtcNow;
        }
        if (retryAfter is null) return null;
        return retryAfter <= TimeSpan.Zero
            ? TimeSpan.Zero
            : retryAfter > MaximumRetryAfter
                ? MaximumRetryAfter
                : retryAfter;
    }

    private static TorrentFileFetchException Failure(
        TorrentFileFetchFailure failure,
        TimeSpan? retryAfter = null) =>
        new(failure, retryAfter);
}
