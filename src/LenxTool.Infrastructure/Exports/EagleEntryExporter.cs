using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 将经过 URL、类型、魔数和实际字节数校验的图片投递到用户显式配置的本机 Eagle。
/// 每次执行都重读 ACTIVE 策略和目标，防止耐久队列绕过撤销或端点变更。
/// </summary>
public sealed class EagleEntryExporter : IEntryExporter
{
    private const int BufferSize = 80 * 1024;
    private const int MaximumTitleLength = 255;
    private const int MaximumTagCount = 32;
    private const int MaximumTagLength = 64;
    private const long MaximumImageBytes = 12L * 1024 * 1024;
    private readonly IEagleExportTargetStore _targets;
    private readonly IEntryIntegrationPolicyService _policies;
    private readonly IArticleImageStreamDownloader _images;
    private readonly IEagleApiClient _api;

    public const string ExporterId = "eagle";

    public EagleEntryExporter(
        IEagleExportTargetStore targets,
        IEntryIntegrationPolicyService policies,
        IArticleImageStreamDownloader images,
        IEagleApiClient api)
    {
        _targets = targets
            ?? throw new ArgumentNullException(nameof(targets));
        _policies = policies
            ?? throw new ArgumentNullException(nameof(policies));
        _images = images
            ?? throw new ArgumentNullException(nameof(images));
        _api = api
            ?? throw new ArgumentNullException(nameof(api));
    }

    public EntryExportCapability Capability { get; } = new(
        ExporterId,
        "Eagle",
        Array.AsReadOnly([EntryViewKind.Picture]),
        RequiresCredentials: false,
        MaximumImageBytes,
        IsIdempotent: true);

    public async Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);

        // 策略必须先于设置、下载和本机 API 读取，撤销后不再产生任何外部副作用。
        EntryIntegrationPolicySnapshot snapshot;
        try
        {
            snapshot = await _policies.GetAsync(
                    EntryIntegrationPolicyScope.Active,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (AppException exception)
        {
            throw MapPolicyFailure(exception);
        }
        catch (HttpRequestException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (UnauthorizedAccessException)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        catch (IOException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }

        if (!snapshot.Policies.Any(policy =>
                policy.Kind == EntryIntegrationKind.Eagle
                && policy.IsEnabled))
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }

        // 目标快照和租约必须一起取得并持有到最后一次 Eagle 调用结束；
        // 同进程设置保存会等待本代导出完成，避免端点在执行中途换代。
        await using IEagleExportTargetLease targetLease =
            await AcquireCurrentTargetLeaseAsync(cancellationToken)
                .ConfigureAwait(false);
        EagleExportTarget target = NormalizeCurrentTarget(
            targetLease.Target);
        if (!target.MatchesQueueEndpoint(request.TargetId))
        {
            // Eagle 没有可安全迁移的旧 default 任务；端点变化必须重新入队。
            throw Failure(EntryExportErrorCode.Conflict);
        }

        FeedAttachmentClassification attachment =
            SelectSupportedAttachment(request.Entry)
            ?? throw Failure(EntryExportErrorCode.UnsupportedContent);
        if (attachment.Length > MaximumImageBytes)
        {
            throw Failure(EntryExportErrorCode.ContentTooLarge);
        }

        await EnsureCurrentLibraryScopeAsync(
                request.TargetId,
                target,
                cancellationToken)
            .ConfigureAwait(false);

        string stableItemId = CreateStableItemId(
            request.IdempotencyKey);
        if (await ExistsInEagleAsync(
                target.Endpoint,
                stableItemId,
                cancellationToken)
            .ConfigureAwait(false))
        {
            // item/get 无法原子绑定资源库；用第二次探测包围只读查询，
            // 避免把切库后碰巧存在的同 ID 条目误判为当前任务完成。
            await EnsureCurrentLibraryScopeAsync(
                    request.TargetId,
                    target,
                    cancellationToken)
                .ConfigureAwait(false);
            return EntryExportResult.Success(
                request.IdempotencyKey,
                stableItemId,
                remoteUrl: null);
        }

        byte[] bytes;
        string mimeType;
        try
        {
            var budget = new ArticleImageDownloadBudget(
                maximumResources: 1,
                maximumNetworkBytes: MaximumImageBytes);
            await using ArticleImageStreamContent? content =
                await _images.OpenAsync(
                        request.Entry.Id,
                        attachment.SafeUrl!,
                        request.Entry.NormalizedUrl,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (content is null)
            {
                throw Failure(
                    EntryExportErrorCode.DestinationUnavailable,
                    isRetryable: true);
            }

            mimeType = NormalizeImageMimeType(content.MimeType)
                ?? throw Failure(
                    EntryExportErrorCode.UnsupportedContent);
            string? declaredMime = NormalizeImageMimeType(
                attachment.NormalizedMediaType);
            if (!string.Equals(
                    mimeType,
                    declaredMime,
                    StringComparison.Ordinal))
            {
                throw Failure(EntryExportErrorCode.UnsupportedContent);
            }

            bytes = await ReadBoundedAsync(
                    content.Stream,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (EntryExportException)
        {
            throw;
        }
        catch (AppException exception)
        {
            throw MapImageFailure(exception);
        }
        catch (InvalidDataException)
        {
            throw Failure(EntryExportErrorCode.UnsupportedContent);
        }
        catch (UnauthorizedAccessException)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        catch (HttpRequestException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (IOException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }

        if (!MatchesMimeType(mimeType, bytes))
        {
            throw Failure(EntryExportErrorCode.UnsupportedContent);
        }

        // 下载可能持续数秒；在真正写入 Eagle 前再次确认当前资源库，
        // 尽量收窄外部应用在任务执行期间切库造成的误投窗口。
        await EnsureCurrentLibraryScopeAsync(
                request.TargetId,
                target,
                cancellationToken)
            .ConfigureAwait(false);

        var item = new EagleAddItem(
            stableItemId,
            $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}",
            NormalizeTitle(request.Entry.Title, request.Entry.Id),
            NormalizeWebsite(request.Entry.NormalizedUrl),
            NormalizeTags(request.Entry.Categories));

        try
        {
            // AddAsync 会再次按稳定 ID 预检并在不确定 POST 后复查，
            // 同时覆盖并发投递与“已写入但响应丢失”的至少一次语义。
            string remoteId = await _api.AddAsync(
                    target.Endpoint,
                    item,
                    cancellationToken)
                .ConfigureAwait(false);

            // 官方 item/add 不接受资源库身份参数，写前探测不能形成原子条件写。
            // 写后再次探测可把持续切库的结果降级为冲突，但无法撤销已发生的写入。
            await EnsureCurrentLibraryScopeAsync(
                    request.TargetId,
                    target,
                    cancellationToken)
                .ConfigureAwait(false);
            return EntryExportResult.Success(
                request.IdempotencyKey,
                remoteId,
                remoteUrl: null);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (EagleApiException exception)
        {
            // API 异常可能含第三方响应正文；只保留结构化分类，不挂内部异常链。
            throw MapEagleFailure(exception);
        }
        catch (HttpRequestException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (IOException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (ArgumentException)
        {
            throw Failure(EntryExportErrorCode.ProviderRejected);
        }
    }

    private async Task<bool> ExistsInEagleAsync(
        Uri endpoint,
        string itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            // 资源库作用域已经由紧邻的实时探测确认；已存在的重放无需再次下载原图。
            return await _api.ExistsAsync(
                    endpoint,
                    itemId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (EagleApiException exception)
        {
            throw MapEagleFailure(exception);
        }
        catch (HttpRequestException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (IOException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (ArgumentException)
        {
            throw Failure(EntryExportErrorCode.ProviderRejected);
        }
    }

    private async Task<EagleApiCapability> ProbeEagleAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _api.ProbeAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (EagleApiException exception)
        {
            throw MapEagleFailure(exception);
        }
        catch (HttpRequestException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (IOException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (ArgumentException)
        {
            throw Failure(EntryExportErrorCode.ProviderRejected);
        }
    }

    private async Task EnsureCurrentLibraryScopeAsync(
        string queueTargetId,
        EagleExportTarget target,
        CancellationToken cancellationToken)
    {
        EagleApiCapability capability = await ProbeEagleAsync(
                target.Endpoint,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                queueTargetId,
                target.CreateQueueTargetId(capability.LibraryRevision),
                StringComparison.Ordinal))
        {
            // 同一端点可在排队或下载期间切换资源库；一旦探测到作用域不同，
            // 旧任务必须以冲突关闭。官方写接口不支持原子绑定资源库，残余窗口见文档。
            throw Failure(EntryExportErrorCode.Conflict);
        }
    }

    private async Task<IEagleExportTargetLease> AcquireCurrentTargetLeaseAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _targets.AcquireExportLeaseAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (EntryExportException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        catch (IOException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (ArgumentException)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        catch (InvalidOperationException)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
    }

    private static EagleExportTarget NormalizeCurrentTarget(
        EagleExportTarget? configured)
    {
        if (configured is null)
        {
            throw Failure(EntryExportErrorCode.DestinationUnavailable);
        }

        try
        {
            return AppSettingsEagleExportTargetStore.Normalize(configured);
        }
        catch (ArgumentException)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        catch (InvalidOperationException)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
    }

    private static void ValidateRequest(EntryExportRequest request)
    {
        if (!string.Equals(
                request.ExporterId,
                ExporterId,
                StringComparison.Ordinal)
            || request.ViewKind != EntryViewKind.Picture
            || !EagleExportTarget.IsSupportedQueueTargetId(
                request.TargetId)
            || request.Entry is null
            || request.Entry.Enclosures is null
            || request.Entry.Categories is null
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        if (request.ContentBytes < 0)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        if (request.ContentBytes > MaximumImageBytes)
        {
            throw Failure(EntryExportErrorCode.ContentTooLarge);
        }
    }

    /// <summary>
    /// 返回首个同时满足 URL、声明类型和首版 Eagle 格式白名单的图片附件。
    /// App 入队入口与后台执行共用该判定，避免按钮能力与实际适配器漂移。
    /// </summary>
    public static FeedAttachmentClassification? SelectSupportedAttachment(
        FeedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (FeedEnclosure enclosure in entry.Enclosures)
        {
            FeedAttachmentClassification candidate =
                FeedAttachmentClassifier.Classify(
                    enclosure,
                    entry.NormalizedUrl);
            if (candidate.UrlStatus == FeedAttachmentUrlStatus.Allowed
                && candidate.TypeStatus
                    == FeedAttachmentTypeStatus.Verified
                && candidate.Kind == FeedAttachmentKind.Image
                && candidate.SafeUrl is not null
                && candidate.Length is null or <= MaximumImageBytes
                && NormalizeImageMimeType(
                    candidate.NormalizedMediaType) is not null)
            {
                return candidate;
            }
        }
        return null;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int read = await stream.ReadAsync(
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > MaximumImageBytes)
            {
                throw Failure(EntryExportErrorCode.ContentTooLarge);
            }
            output.Write(buffer, 0, read);
        }
    }

    private static string? NormalizeImageMimeType(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "image/png" => "image/png",
            "image/jpg" or "image/pjpeg" or "image/jpeg" =>
                "image/jpeg",
            "image/gif" => "image/gif",
            "image/webp" => "image/webp",
            "image/bmp" or "image/x-ms-bmp" => "image/bmp",
            _ => null
        };

    private static bool MatchesMimeType(
        string mimeType,
        ReadOnlySpan<byte> bytes) =>
        mimeType switch
        {
            "image/png" => bytes.StartsWith(
                new byte[]
                {
                    0x89, 0x50, 0x4E, 0x47,
                    0x0D, 0x0A, 0x1A, 0x0A
                }),
            "image/jpeg" => bytes.StartsWith(
                new byte[] { 0xFF, 0xD8, 0xFF }),
            "image/gif" => bytes.StartsWith("GIF87a"u8)
                           || bytes.StartsWith("GIF89a"u8),
            "image/webp" => bytes.Length >= 12
                            && bytes[..4].SequenceEqual("RIFF"u8)
                            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            "image/bmp" => bytes.StartsWith("BM"u8),
            _ => false
        };

    private static string CreateStableItemId(string idempotencyKey)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(idempotencyKey));
        return $"LT{Convert.ToHexString(hash.AsSpan(0, 15))}";
    }

    private static string NormalizeTitle(
        string? title,
        string entryId)
    {
        string normalized = NormalizeSingleLine(title);
        if (normalized.Length == 0)
        {
            normalized = NormalizeSingleLine(entryId);
        }
        if (normalized.Length == 0)
        {
            normalized = "LenxTool 图片";
        }
        if (normalized.Length <= MaximumTitleLength)
        {
            return normalized;
        }

        int length = MaximumTitleLength;
        if (char.IsHighSurrogate(normalized[length - 1]))
        {
            length--;
        }
        return normalized[..length];
    }

    private static ReadOnlyCollection<string> NormalizeTags(
        IReadOnlyList<string> categories)
    {
        var tags = new List<string>(
            Math.Min(categories.Count, MaximumTagCount));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? category in categories)
        {
            string tag = NormalizeSingleLine(category);
            if (tag.Length > MaximumTagLength)
            {
                tag = tag[..MaximumTagLength];
            }
            if (tag.Length > 0 && seen.Add(tag))
            {
                tags.Add(tag);
                if (tags.Count == MaximumTagCount)
                {
                    break;
                }
            }
        }
        return Array.AsReadOnly(tags.ToArray());
    }

    private static string NormalizeSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        char[] characters = value
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray();
        return new string(characters).Trim();
    }

    private static string? NormalizeWebsite(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.AbsoluteUri.Length > 2048
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        return uri.AbsoluteUri;
    }

    private static EntryExportException MapPolicyFailure(
        AppException exception)
    {
        EntryExportErrorCode code = exception.Error.Code
            is AppErrorCode.AccessDenied
                or AppErrorCode.CredentialsInvalid
            ? EntryExportErrorCode.AccessDenied
            : EntryExportErrorCode.DestinationUnavailable;
        return Failure(code, exception.Error.IsRetryable);
    }

    private static EntryExportException MapImageFailure(
        AppException exception)
    {
        EntryExportErrorCode code = exception.Error.Code switch
        {
            AppErrorCode.AccessDenied
                or AppErrorCode.CredentialsInvalid =>
                EntryExportErrorCode.AccessDenied,
            AppErrorCode.ProviderRateLimited =>
                EntryExportErrorCode.RateLimited,
            AppErrorCode.InvalidRequest =>
                EntryExportErrorCode.UnsupportedContent,
            _ => EntryExportErrorCode.DestinationUnavailable
        };
        return Failure(code, exception.Error.IsRetryable);
    }

    private static EntryExportException MapEagleFailure(
        EagleApiException exception)
    {
        EntryExportErrorCode code = exception.Failure switch
        {
            EagleApiFailure.Unavailable =>
                EntryExportErrorCode.DestinationUnavailable,
            EagleApiFailure.Incompatible
                or EagleApiFailure.Rejected =>
                EntryExportErrorCode.ProviderRejected,
            _ => EntryExportErrorCode.Unknown
        };
        return Failure(code, exception.IsRetryable);
    }

    private static EntryExportException Failure(
        EntryExportErrorCode code,
        bool isRetryable = false) =>
        new(new(code, isRetryable));
}
