using System.Collections.Concurrent;
using System.Net;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal sealed class CachedArticleImageDownloader
    : IArticleImageDownloader, IArticleImageStreamDownloader, IDisposable
{
    private const int BufferSize = 80 * 1024;
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private readonly IEntryAssetStore _assetStore;
    private readonly IArticleImageTransport _transport;
    private readonly ArticleImageDownloadOptions _options;
    private readonly AssetCacheOptions _cacheOptions;
    private readonly TimeProvider _timeProvider;
    private readonly FeedNetworkPolicy _networkPolicy;
    private readonly SemaphoreSlim _downloadSlots;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentFailures =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public CachedArticleImageDownloader(
        IEntryAssetStore assetStore,
        IFeedHostResolver resolver,
        IArticleImageTransport transport,
        FeedDiscoveryOptions feedOptions,
        ArticleImageDownloadOptions options,
        AssetCacheOptions cacheOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(assetStore);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(feedOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cacheOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateOptions(options);
        if (cacheOptions.MaximumAssetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheOptions));
        }

        _assetStore = assetStore;
        _transport = transport;
        _options = options;
        _cacheOptions = cacheOptions;
        _timeProvider = timeProvider;
        _networkPolicy = new(resolver, feedOptions);
        _downloadSlots = new(options.MaximumConcurrentDownloads);
    }

    public async Task<ArticleImageContent?> GetAsync(
        string entryId,
        string imageUrl,
        string? referrer,
        ArticleImageDownloadBudget budget,
        CancellationToken cancellationToken)
    {
        await using ArticleImageStreamContent? content = await OpenAsync(
            entryId,
            imageUrl,
            referrer,
            budget,
            cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }
        byte[] bytes = await ReadBoundedAsync(
            content.Stream,
            _cacheOptions.MaximumAssetBytes,
            budget: null,
            cancellationToken).ConfigureAwait(false);
        return new(bytes, content.MimeType, content.FromCache);
    }

    public async Task<ArticleImageStreamContent?> OpenAsync(
        string entryId,
        string imageUrl,
        string? referrer,
        ArticleImageDownloadBudget budget,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (entryId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(entryId));
        }
        ArgumentNullException.ThrowIfNull(budget);
        Uri requestedUri = _networkPolicy.ParseAndValidate(imageUrl);
        string normalizedUrl = requestedUri.AbsoluteUri;
        if (!budget.TryReserveResource(normalizedUrl))
        {
            return null;
        }

        ArticleImageStreamContent? cached = await OpenCachedAsync(
            entryId,
            normalizedUrl,
            fromCache: true,
            cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        string failureKey = $"{entryId}\n{normalizedUrl}";
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_recentFailures.TryGetValue(failureKey, out DateTimeOffset retryAt))
        {
            if (retryAt > now)
            {
                return null;
            }
            _recentFailures.TryRemove(failureKey, out _);
        }

        await _downloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(_options.TotalTimeout);
            try
            {
                await DownloadToCacheAsync(
                    entryId,
                    requestedUri,
                    NormalizeReferrer(referrer),
                    budget,
                    timeout.Token).ConfigureAwait(false);
                _recentFailures.TryRemove(failureKey, out _);
                return await OpenCachedAsync(
                    entryId,
                    normalizedUrl,
                    fromCache: false,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("图片缓存写入后无法读取。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _recentFailures[failureKey] =
                    _timeProvider.GetUtcNow() + _options.FailureRetryDelay;
                throw;
            }
        }
        finally
        {
            _downloadSlots.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _downloadSlots.Dispose();
    }

    private async Task<ArticleImageStreamContent?> OpenCachedAsync(
        string entryId,
        string sourceUrl,
        bool fromCache,
        CancellationToken cancellationToken)
    {
        EntryAsset? asset = await _assetStore.GetAsync(
            entryId,
            sourceUrl,
            cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return null;
        }

        Stream? stream = await _assetStore.OpenReadAsync(
            asset,
            cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }
        try
        {
            byte[] prefix = await ReadPrefixAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesMimeType(asset.MimeType, prefix))
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            return new(
                new PrefixReplayStream(prefix, stream),
                NormalizeMimeType(asset.MimeType),
                fromCache);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task DownloadToCacheAsync(
        string entryId,
        Uri initialUri,
        Uri? referrer,
        ArticleImageDownloadBudget budget,
        CancellationToken cancellationToken)
    {
        Uri current = _networkPolicy.ParseAndValidate(initialUri.AbsoluteUri);
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            current.AbsoluteUri
        };
        int redirects = 0;
        while (true)
        {
            IReadOnlyList<IPAddress> addresses = await _networkPolicy
                .ResolveAllowedAsync(current, cancellationToken)
                .ConfigureAwait(false);
            using ArticleImageHttpResponse ownedResponse = await _transport
                .SendAsync(current, addresses, referrer, cancellationToken)
                .ConfigureAwait(false);
            HttpResponseMessage response = ownedResponse.Message;
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= _options.MaximumRedirects
                    || response.Headers.Location is null)
                {
                    throw new InvalidDataException(
                        "图片重定向次数过多或缺少目标地址。");
                }

                Uri redirected;
                try
                {
                    redirected = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                }
                catch (UriFormatException exception)
                {
                    throw new InvalidDataException("图片重定向地址无效。", exception);
                }

                current = _networkPolicy.ParseAndValidate(redirected.AbsoluteUri);
                if (!visited.Add(current.AbsoluteUri))
                {
                    throw new InvalidDataException("图片重定向形成循环。");
                }
                redirects++;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppException(AppErrorFactory.FromHttp(
                    response.StatusCode,
                    "图片下载"));
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!IsSupportedMimeType(mediaType))
            {
                throw new InvalidDataException("图片响应 MIME 类型不受支持。");
            }
            long maximumBytes = Math.Min(
                _cacheOptions.MaximumAssetBytes,
                budget.RemainingNetworkBytes);
            if (maximumBytes <= 0
                || response.Content.Headers.ContentLength > maximumBytes)
            {
                throw new InvalidDataException("图片超过允许的大小或文章带宽预算。");
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            string normalizedMimeType = NormalizeMimeType(mediaType!);
            byte[] prefix = await ReadPrefixAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesMimeType(normalizedMimeType, prefix))
            {
                throw new InvalidDataException("图片内容与声明的 MIME 类型不匹配。");
            }

            await using var replay = new PrefixReplayStream(prefix, stream, leaveOpen: true);
            await using var cachedContent = new BudgetBoundedReadStream(
                replay,
                maximumBytes,
                budget);
            await _assetStore.PutAsync(
                entryId,
                initialUri.AbsoluteUri,
                normalizedMimeType,
                cachedContent,
                cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private static async Task<byte[]> ReadPrefixAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[12];
        int total = 0;
        while (total < prefix.Length)
        {
            int read = await stream.ReadAsync(
                prefix.AsMemory(total),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total == prefix.Length ? prefix : prefix[..total];
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long maximumBytes,
        ArticleImageDownloadBudget? budget,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > maximumBytes
                || (budget is not null && !budget.TryConsumeNetworkBytes(read)))
            {
                throw new InvalidDataException(
                    "图片超过允许的大小或文章带宽预算。");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static Uri? NormalizeReferrer(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static bool IsSupportedMimeType(string? mediaType) =>
        mediaType is not null
        && NormalizeMimeType(mediaType) is
            "image/png"
            or "image/jpeg"
            or "image/gif"
            or "image/bmp"
            or "image/webp";

    private static string NormalizeMimeType(string mediaType) =>
        mediaType.Trim().ToLowerInvariant() switch
        {
            "image/jpg" or "image/pjpeg" => "image/jpeg",
            "image/x-ms-bmp" => "image/bmp",
            var normalized => normalized
        };

    private static bool MatchesMimeType(string mediaType, ReadOnlySpan<byte> bytes)
    {
        string normalized = NormalizeMimeType(mediaType);
        return normalized switch
        {
            "image/png" => bytes.StartsWith(PngSignature),
            "image/jpeg" => bytes.StartsWith(JpegSignature),
            "image/gif" => bytes.StartsWith("GIF87a"u8)
                           || bytes.StartsWith("GIF89a"u8),
            "image/bmp" => bytes.StartsWith("BM"u8),
            "image/webp" => bytes.Length >= 12
                            && bytes[..4].SequenceEqual("RIFF"u8)
                            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private sealed class PrefixReplayStream(
        byte[] prefix,
        Stream inner,
        bool leaveOpen = false) : Stream
    {
        private int _prefixOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int copied = Math.Min(buffer.Length, prefix.Length - _prefixOffset);
            if (copied > 0)
            {
                prefix.AsMemory(_prefixOffset, copied).CopyTo(buffer);
                _prefixOffset += copied;
            }
            if (copied == buffer.Length)
            {
                return copied;
            }
            return copied + await inner.ReadAsync(
                buffer[copied..],
                cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen) inner.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BudgetBoundedReadStream(
        Stream inner,
        long maximumBytes,
        ArticleImageDownloadBudget budget) : Stream
    {
        private long _totalRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_totalRead == maximumBytes)
            {
                byte[] overflowProbe = new byte[1];
                int overflow = await inner.ReadAsync(
                    overflowProbe,
                    cancellationToken).ConfigureAwait(false);
                if (overflow == 0) return 0;
                throw new InvalidDataException("图片超过允许的大小或文章带宽预算。");
            }

            int allowed = (int)Math.Min(buffer.Length, maximumBytes - _totalRead);
            int read = await inner.ReadAsync(
                buffer[..allowed],
                cancellationToken).ConfigureAwait(false);
            if (read > 0 && !budget.TryConsumeNetworkBytes(read))
            {
                throw new InvalidDataException("图片超过允许的大小或文章带宽预算。");
            }
            _totalRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void ValidateOptions(ArticleImageDownloadOptions options)
    {
        if (options.TotalTimeout <= TimeSpan.Zero
            || options.MaximumRedirects < 0
            || options.MaximumConcurrentDownloads <= 0
            || options.FailureRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
