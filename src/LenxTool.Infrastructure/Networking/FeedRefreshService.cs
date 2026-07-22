using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal sealed class FeedRefreshService : IFeedRefreshService, IDisposable
{
    private readonly IFeedFetchStateRepository _repository;
    private readonly IFeedEntryWriter _entryWriter;
    private readonly IFeedParser _parser;
    private readonly IFeedRefreshTransport _transport;
    private readonly FeedDiscoveryOptions _networkOptions;
    private readonly FeedRefreshOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly FeedNetworkPolicy _networkPolicy;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _feedGates = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _initializationGate = new();
    private Task? _backgroundTask;
    private bool _disposed;

    public FeedRefreshService(
        IFeedFetchStateRepository repository,
        IFeedEntryWriter entryWriter,
        IFeedParser parser,
        IFeedHostResolver resolver,
        IFeedRefreshTransport transport,
        FeedDiscoveryOptions networkOptions,
        FeedRefreshOptions options,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _entryWriter = entryWriter;
        _parser = parser;
        _transport = transport;
        _networkOptions = ValidateNetworkOptions(networkOptions);
        _options = ValidateOptions(options);
        _timeProvider = timeProvider;
        _networkPolicy = new(resolver, networkOptions);
        _concurrency = new(options.MaximumConcurrency, options.MaximumConcurrency);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_initializationGate)
        {
            _backgroundTask ??= RunBackgroundAsync(_shutdown.Token);
        }
        return Task.CompletedTask;
    }

    public async Task<FeedRefreshResult> RefreshAsync(
        string feedId,
        bool force,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Guid.TryParseExact(feedId, "D", out _))
            throw new ArgumentException("Feed ID must be a canonical GUID.", nameof(feedId));

        SemaphoreSlim gate = _feedGates.GetOrAdd(feedId, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FeedRefreshTarget? target = await _repository.GetTargetAsync(feedId, cancellationToken)
                .ConfigureAwait(false);
            if (target is null)
            {
                return new(feedId, FeedRefreshOutcome.SkippedUnavailable, 0, null, null);
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (!force && target.State?.NextFetchAt is DateTimeOffset next && next > now)
            {
                return new(feedId, FeedRefreshOutcome.SkippedNotDue, 0, next, null);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_networkOptions.TotalTimeout);
            try
            {
                return await FetchAndProcessAsync(target, now, timeout.Token, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return await RecordFailureAsync(target, now, "timeout", null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AppException exception)
            {
                string code = string.Equals(
                    exception.Error.Provider,
                    "Feed 解析",
                    StringComparison.Ordinal)
                    ? "invalid_feed"
                    : "unsafe_endpoint";
                return await RecordFailureAsync(target, now, code, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return await RecordFailureAsync(target, now, "invalid_response", null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                return await RecordFailureAsync(target, now, "network", null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FeedRefreshBatchResult> RefreshDueAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<FeedRefreshTarget> due = await _repository.GetDueTargetsAsync(
            _timeProvider.GetUtcNow(),
            _options.MaximumFeedsPerPass,
            cancellationToken).ConfigureAwait(false);
        FeedRefreshResult[] results = await Task.WhenAll(due.Select(async target =>
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RefreshAsync(target.Feed.Id, force: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _concurrency.Release();
            }
        })).ConfigureAwait(false);
        return new(
            results.Length,
            results.Count(result => result.Outcome == FeedRefreshOutcome.Updated),
            results.Count(result => result.Outcome == FeedRefreshOutcome.NotModified),
            results.Count(result => result.Outcome == FeedRefreshOutcome.Failed));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
    }

    private async Task<FeedRefreshResult> FetchAndProcessAsync(
        FeedRefreshTarget target,
        DateTimeOffset now,
        CancellationToken requestCancellationToken,
        CancellationToken persistenceCancellationToken)
    {
        Uri initial = _networkPolicy.ParseAndValidate(target.Feed.NormalizedUrl);
        Uri current = initial;
        var visited = new HashSet<string>(StringComparer.Ordinal) { current.AbsoluteUri };
        int redirects = 0;
        while (true)
        {
            IReadOnlyList<IPAddress> addresses = await _networkPolicy
                .ResolveAllowedAsync(current, requestCancellationToken)
                .ConfigureAwait(false);
            bool sameAuthority = Uri.Compare(
                initial,
                current,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0;
            var request = new FeedRefreshHttpRequest(
                sameAuthority ? target.State?.ETag : null,
                sameAuthority ? target.State?.LastModified : null);
            using FeedRefreshHttpResponse ownedResponse = await _transport
                .SendAsync(current, addresses, request, requestCancellationToken)
                .ConfigureAwait(false);
            HttpResponseMessage response = ownedResponse.Message;

            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= _networkOptions.MaximumRedirects || response.Headers.Location is null)
                    throw new InvalidDataException("Feed redirect is invalid or exceeds the configured limit.");
                Uri redirected;
                try
                {
                    redirected = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                }
                catch (UriFormatException exception)
                {
                    throw new InvalidDataException("Feed redirect target is invalid.", exception);
                }
                redirected = _networkPolicy.ParseAndValidate(redirected.AbsoluteUri);
                if (!visited.Add(redirected.AbsoluteUri))
                    throw new InvalidDataException("Feed redirect loop detected.");
                current = redirected;
                redirects++;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (request.ETag is null && request.LastModified is null)
                    throw new InvalidDataException("An unconditional Feed request returned 304.");
                FeedFetchState state = SuccessfulState(target, response, now);
                bool saved = await _repository.SaveStateAsync(state, persistenceCancellationToken)
                    .ConfigureAwait(false);
                return saved
                    ? new(target.Feed.Id, FeedRefreshOutcome.NotModified, 0, state.NextFetchAt, null)
                    : new(target.Feed.Id, FeedRefreshOutcome.SkippedUnavailable, 0, null, null);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                TimeSpan? retryAfter = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? ReadRetryAfter(response, now)
                    : null;
                return await RecordFailureAsync(
                    target,
                    now,
                    $"http_{(int)response.StatusCode}",
                    retryAfter,
                    persistenceCancellationToken).ConfigureAwait(false);
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || !IsXmlMediaType(mediaType))
                throw new InvalidDataException("Feed response MIME type is unsupported.");
            byte[] content = await ReadContentAsync(response.Content, requestCancellationToken)
                .ConfigureAwait(false);
            ParsedFeedDocument parsed = _parser.Parse(
                target.Feed.Id,
                current.AbsoluteUri,
                content,
                now);
            try
            {
                await _entryWriter.UpsertAsync(
                    target.Feed.Id,
                    parsed.Entries,
                    persistenceCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (persistenceCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return await RecordFailureAsync(
                    target,
                    now,
                    "storage",
                    null,
                    persistenceCancellationToken).ConfigureAwait(false);
            }
            FeedFetchState success = SuccessfulState(target, response, now);
            bool persisted = await _repository.SaveStateAsync(success, persistenceCancellationToken)
                .ConfigureAwait(false);
            return persisted
                ? new(
                    target.Feed.Id,
                    FeedRefreshOutcome.Updated,
                    parsed.Entries.Count,
                    success.NextFetchAt,
                    null)
                : new(target.Feed.Id, FeedRefreshOutcome.SkippedUnavailable, 0, null, null);
        }
    }

    private static FeedFetchState SuccessfulState(
        FeedRefreshTarget target,
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        string? etag = BoundedHeader(response.Headers.ETag?.ToString(), 1024)
            ?? BoundedHeader(target.State?.ETag, 1024);
        string? lastModified = BoundedHeader(
            response.Content.Headers.LastModified?.ToString("R"),
            256) ?? BoundedHeader(target.State?.LastModified, 256);
        return new(
            target.Feed.Id,
            etag,
            lastModified,
            now.AddMinutes(target.Feed.RefreshIntervalMinutes),
            now,
            target.State?.LastFailureAt,
            0,
            null,
            now);
    }

    private async Task<FeedRefreshResult> RecordFailureAsync(
        FeedRefreshTarget target,
        DateTimeOffset now,
        string errorCode,
        TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        int failures = target.State?.ConsecutiveFailures >= int.MaxValue
            ? int.MaxValue
            : (target.State?.ConsecutiveFailures ?? 0) + 1;
        TimeSpan delay = CalculateFailureDelay(failures, retryAfter);
        var state = new FeedFetchState(
            target.Feed.Id,
            target.State?.ETag,
            target.State?.LastModified,
            now.Add(delay),
            target.State?.LastSuccessAt,
            now,
            failures,
            errorCode,
            now);
        bool saved = await _repository.SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        return saved
            ? new(target.Feed.Id, FeedRefreshOutcome.Failed, 0, state.NextFetchAt, errorCode)
            : new(target.Feed.Id, FeedRefreshOutcome.SkippedUnavailable, 0, null, null);
    }

    private TimeSpan CalculateFailureDelay(int failures, TimeSpan? retryAfter)
    {
        int exponent = Math.Min(Math.Max(failures - 1, 0), 30);
        double multiplier = Math.Pow(2, exponent);
        double ticks = Math.Min(
            _options.InitialFailureDelay.Ticks * multiplier,
            _options.MaximumFailureDelay.Ticks);
        TimeSpan delay = TimeSpan.FromTicks((long)ticks);
        if (retryAfter is TimeSpan requested && requested > delay)
        {
            delay = requested > _options.MaximumFailureDelay
                ? _options.MaximumFailureDelay
                : requested;
        }
        return delay;
    }

    private async Task<byte[]> ReadContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _networkOptions.MaximumCompressedBytes)
            throw new InvalidDataException("Feed response exceeds the compressed size limit.");
        await using Stream network = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] compressed = await ReadBoundedAsync(
            network,
            _networkOptions.MaximumCompressedBytes,
            cancellationToken).ConfigureAwait(false);
        string[] encodings = content.Headers.ContentEncoding.ToArray();
        if (encodings.Length == 0
            || (encodings.Length == 1 && encodings[0].Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            if (compressed.Length > _networkOptions.MaximumDecompressedBytes)
                throw new InvalidDataException("Feed response exceeds the decompressed size limit.");
            return compressed;
        }
        if (encodings.Length != 1)
            throw new InvalidDataException("Multiple content encodings are not supported.");

        using var input = new MemoryStream(compressed, writable: false);
        await using Stream decompressor = encodings[0].ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw new InvalidDataException("Unsupported content encoding.")
        };
        return await ReadBoundedAsync(
            decompressor,
            _networkOptions.MaximumDecompressedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Feed response exceeds the configured size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task RunBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await RefreshDueAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A repository-level failure must not terminate later scheduled passes.
                }
                await Task.Delay(_options.SchedulerInterval, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string? BoundedHeader(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            return null;
        }
        return value;
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan delay = date - now;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
        return null;
    }

    private static bool IsXmlMediaType(string mediaType) =>
        mediaType.Equals("application/rss+xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/atom+xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static FeedRefreshOptions ValidateOptions(FeedRefreshOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumConcurrency is < 1 or > 16
            || options.MaximumFeedsPerPass is < 1 or > 1000
            || options.InitialFailureDelay <= TimeSpan.Zero
            || options.MaximumFailureDelay < options.InitialFailureDelay
            || options.MaximumFailureDelay > TimeSpan.FromDays(1)
            || options.SchedulerInterval < TimeSpan.FromSeconds(1)
            || options.SchedulerInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return options;
    }

    private static FeedDiscoveryOptions ValidateNetworkOptions(FeedDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.AllowedHttpHosts);
        ArgumentNullException.ThrowIfNull(options.TrustedPrivateHosts);
        if (options.TotalTimeout <= TimeSpan.Zero
            || options.ConnectTimeout <= TimeSpan.Zero
            || options.ConnectTimeout > options.TotalTimeout
            || options.MaximumRedirects is < 0 or > 10
            || options.MaximumCompressedBytes is < 1024 or > 10 * 1024 * 1024
            || options.MaximumDecompressedBytes < options.MaximumCompressedBytes
            || options.MaximumDecompressedBytes > 20 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return options;
    }
}
