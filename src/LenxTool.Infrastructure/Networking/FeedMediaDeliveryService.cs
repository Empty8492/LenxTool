using System.Net;
using System.Security.Cryptography;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.Infrastructure.Networking;

internal sealed class FeedMediaDeliveryService : IFeedMediaDeliveryService, IDisposable
{
    private const int BufferSize = 80 * 1024;
    private const long MinimumFreeSpaceReserveBytes =
        64L * 1024 * 1024;
    private readonly IFeedMediaDeliveryRepository _repository;
    private readonly IFeedMediaTransport _transport;
    private readonly FeedMediaDeliveryOptions _options;
    private readonly AppPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly FeedNetworkPolicy _networkPolicy;
    private readonly SemaphoreSlim _downloadSlots;
    private readonly object _keyedGatesLock = new();
    private readonly Dictionary<string, KeyedGate> _keyedGates =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public FeedMediaDeliveryService(
        IFeedMediaDeliveryRepository repository,
        IFeedHostResolver resolver,
        IFeedMediaTransport transport,
        FeedDiscoveryOptions feedOptions,
        FeedMediaDeliveryOptions options,
        AppPaths paths,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(feedOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateOptions(options);

        _repository = repository;
        _transport = transport;
        _options = options;
        _paths = paths;
        _timeProvider = timeProvider;
        _networkPolicy = new(resolver, feedOptions);
        _downloadSlots = new(options.MaximumConcurrentDownloads);
    }

    public async Task<FeedMediaDeliveryRegistration> DeliverAsync(
        FeedEntry entry,
        FeedEnclosure enclosure,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(enclosure);

        FeedAttachmentClassification attachment =
            FeedAttachmentClassifier.Classify(enclosure, entry.NormalizedUrl);
        ValidateAttachment(attachment);
        if (attachment.Length > _options.MaximumBytes)
        {
            throw new InvalidDataException(
                $"媒体附件超过 {_options.MaximumBytes} 字节的下载上限。");
        }

        Uri sourceUri = _networkPolicy.ParseAndValidate(attachment.SafeUrl!);
        string sourceUrl = sourceUri.AbsoluteUri;
        string deliveryKey = CreateDeliveryKey(entry.Id, sourceUrl);
        string mediaJobId = $"feed-{deliveryKey}";
        string finalPath = Path.Combine(
            _paths.FeedMediaDirectory,
            deliveryKey + attachment.FileExtension);
        string tempPath = Path.Combine(
            _paths.FeedMediaTempDirectory,
            $"{deliveryKey}.{Guid.NewGuid():N}.part");

        FeedMediaDeliveryRegistration? existing = await _repository.GetAsync(
            entry.Id,
            sourceUrl,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null && File.Exists(existing.Job.InputPath))
        {
            ValidateExistingPath(existing.Job.InputPath, finalPath);
            return existing;
        }

        using KeyedGateLease keyedGate =
            await EnterKeyGateAsync(deliveryKey, cancellationToken).ConfigureAwait(false);
        existing = await _repository.GetAsync(
            entry.Id,
            sourceUrl,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null && File.Exists(existing.Job.InputPath))
        {
            ValidateExistingPath(existing.Job.InputPath, finalPath);
            return existing;
        }
        if (existing is not null)
        {
            ValidateExistingPath(existing.Job.InputPath, finalPath);
        }

        EnsureAvailableDiskSpace(
            attachment.Length ?? _options.MaximumBytes);
        _paths.EnsureCreated();
        await _downloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool movedToFinal = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(_options.TotalTimeout);
            DownloadedMedia downloaded;
            try
            {
                downloaded = await DownloadAsync(
                    sourceUri,
                    attachment.NormalizedMediaType!,
                    tempPath,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("媒体下载超过允许的总时长。", exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, finalPath, overwrite: true);
            movedToFinal = true;

            if (existing is not null)
            {
                return existing;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            var job = new MediaJob(
                mediaJobId,
                "FeedTranscription",
                finalPath,
                null,
                MediaJobStatus.Queued,
                0,
                TranscriptionEngine.Groq,
                "whisper-large-v3",
                0,
                0,
                null,
                now,
                now);
            var delivery = new FeedMediaDelivery(
                entry.Id,
                entry.FeedId,
                entry.Title,
                sourceUrl,
                attachment.Title,
                downloaded.MediaType,
                downloaded.Length,
                mediaJobId,
                now);

            return await _repository.CreateOrGetQueuedAsync(
                delivery,
                job,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(tempPath);
            if (movedToFinal && existing is null)
            {
                TryDelete(finalPath);
            }
            throw;
        }
        finally
        {
            _downloadSlots.Release();
            TryDelete(tempPath);
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

    private async Task<DownloadedMedia> DownloadAsync(
        Uri initialUri,
        string expectedMediaType,
        string tempPath,
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
            using FeedMediaHttpResponse ownedResponse = await _transport.SendAsync(
                current,
                addresses,
                cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response = ownedResponse.Message;
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= _options.MaximumRedirects ||
                    response.Headers.Location is null)
                {
                    throw new InvalidDataException(
                        "媒体重定向次数过多或缺少目标地址。");
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
                    throw new InvalidDataException("媒体重定向地址无效。", exception);
                }

                current = _networkPolicy.ParseAndValidate(redirected.AbsoluteUri);
                if (!visited.Add(current.AbsoluteUri))
                {
                    throw new InvalidDataException("媒体重定向形成循环。");
                }
                redirects++;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppException(AppErrorFactory.FromHttp(
                    response.StatusCode,
                    "媒体下载"));
            }

            string? responseMediaType =
                NormalizeMediaType(response.Content.Headers.ContentType?.MediaType);
            if (!string.Equals(
                    responseMediaType,
                    NormalizeMediaType(expectedMediaType),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "媒体响应类型与 Feed 声明的类型不一致。");
            }
            if (response.Content.Headers.ContentLength is long declaredLength &&
                declaredLength > _options.MaximumBytes)
            {
                throw new InvalidDataException(
                    $"媒体响应超过 {_options.MaximumBytes} 字节的下载上限。");
            }
            if (response.Content.Headers.ContentLength is long responseLength)
            {
                EnsureAvailableDiskSpace(responseLength);
            }

            await using Stream input = await response.Content.ReadAsStreamAsync(
                cancellationToken).ConfigureAwait(false);
            long length = await CopyBoundedAsync(
                input,
                tempPath,
                cancellationToken).ConfigureAwait(false);
            if (!MatchesMediaSignature(responseMediaType!, tempPath))
            {
                throw new InvalidDataException(
                    "媒体内容与声明的类型不匹配。");
            }
            return new(responseMediaType!, length);
        }
    }

    private async Task<long> CopyBoundedAsync(
        Stream input,
        string tempPath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(
                buffer.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total = checked(total + read);
            if (total > _options.MaximumBytes)
            {
                throw new InvalidDataException(
                    $"媒体响应超过 {_options.MaximumBytes} 字节的下载上限。");
            }
            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return total;
    }

    private static bool MatchesMediaSignature(string mediaType, string path)
    {
        Span<byte> signature = stackalloc byte[16];
        using FileStream stream = File.OpenRead(path);
        int length = stream.Read(signature);
        ReadOnlySpan<byte> bytes = signature[..length];
        return mediaType switch
        {
            "audio/mpeg" =>
                bytes.StartsWith("ID3"u8) ||
                (bytes.Length >= 2 &&
                 bytes[0] == 0xFF &&
                 (bytes[1] & 0xE0) == 0xE0),
            "audio/mp4" or "video/mp4" or "video/quicktime" =>
                bytes.Length >= 8 &&
                bytes.Slice(4, 4).SequenceEqual("ftyp"u8),
            "audio/aac" =>
                bytes.Length >= 2 &&
                bytes[0] == 0xFF &&
                (bytes[1] & 0xF6) == 0xF0,
            "audio/ogg" or "audio/opus" or "video/ogg" =>
                bytes.StartsWith("OggS"u8),
            "audio/wav" =>
                bytes.Length >= 12 &&
                bytes[..4].SequenceEqual("RIFF"u8) &&
                bytes.Slice(8, 4).SequenceEqual("WAVE"u8),
            "audio/flac" => bytes.StartsWith("fLaC"u8),
            "video/webm" =>
                bytes.Length >= 4 &&
                bytes[0] == 0x1A &&
                bytes[1] == 0x45 &&
                bytes[2] == 0xDF &&
                bytes[3] == 0xA3,
            _ => false
        };
    }

    private static string? NormalizeMediaType(string? mediaType) =>
        mediaType?.Trim().ToLowerInvariant() switch
        {
            "audio/x-m4a" => "audio/mp4",
            "audio/x-wav" => "audio/wav",
            { Length: > 0 } normalized => normalized,
            _ => null
        };

    private static void ValidateAttachment(
        FeedAttachmentClassification attachment)
    {
        if (attachment.UrlStatus != FeedAttachmentUrlStatus.Allowed ||
            attachment.SafeUrl is null)
        {
            throw new InvalidDataException("媒体附件地址不安全或无效。");
        }
        if (!attachment.IsTypeVerified ||
            attachment.Kind is not (FeedAttachmentKind.Audio or FeedAttachmentKind.Video) ||
            attachment.NormalizedMediaType is null ||
            attachment.FileExtension is null)
        {
            throw new InvalidDataException(
                "媒体附件必须具有相互匹配的受支持 MIME 类型与扩展名。");
        }
    }

    private static string CreateDeliveryKey(string entryId, string sourceUrl)
    {
        byte[] value = Encoding.UTF8.GetBytes($"{entryId}\n{sourceUrl}");
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private static void ValidateExistingPath(
        string existingPath,
        string expectedPath)
    {
        if (!string.Equals(
                Path.GetFullPath(existingPath),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "已登记的 Feed 媒体任务路径与安全下载目录不一致。");
        }
    }

    private async Task<KeyedGateLease> EnterKeyGateAsync(
        string key,
        CancellationToken cancellationToken)
    {
        KeyedGate gate;
        lock (_keyedGatesLock)
        {
            if (!_keyedGates.TryGetValue(key, out gate!))
            {
                gate = new();
                _keyedGates.Add(key, gate);
            }
            gate.ReferenceCount++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(this, key, gate);
        }
        catch
        {
            ReleaseKeyGateReference(key, gate, releaseSemaphore: false);
            throw;
        }
    }

    private void ReleaseKeyGateReference(
        string key,
        KeyedGate gate,
        bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        bool dispose = false;
        lock (_keyedGatesLock)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0 &&
                _keyedGates.TryGetValue(key, out KeyedGate? current) &&
                ReferenceEquals(current, gate))
            {
                _keyedGates.Remove(key);
                dispose = true;
            }
        }
        if (dispose)
        {
            gate.Semaphore.Dispose();
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateOptions(FeedMediaDeliveryOptions options)
    {
        if (options.MaximumBytes <= 0 ||
            options.TotalTimeout <= TimeSpan.Zero ||
            options.MaximumRedirects < 0 ||
            options.MaximumConcurrentDownloads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private void EnsureAvailableDiskSpace(long mediaBytes)
    {
        string fullPath = Path.GetFullPath(
            _paths.FeedMediaTempDirectory);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new IOException(
                "无法确定媒体临时目录所在磁盘。");
        long available = new DriveInfo(root).AvailableFreeSpace;
        long required = mediaBytes
            > long.MaxValue - MinimumFreeSpaceReserveBytes
                ? long.MaxValue
                : mediaBytes + MinimumFreeSpaceReserveBytes;
        if (available < required)
        {
            throw new IOException(
                "媒体下载所需磁盘空间不足。");
        }
    }

    private sealed record DownloadedMedia(string MediaType, long Length);

    private sealed class KeyedGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class KeyedGateLease(
        FeedMediaDeliveryService owner,
        string key,
        KeyedGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseKeyGateReference(
                    key,
                    gate,
                    releaseSemaphore: true);
            }
        }
    }
}
