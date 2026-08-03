using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 将 RSS 条目映射为 Zotero Web API v3 的个人库对象。每次耐久任务执行都会重新读取
/// ACTIVE 策略、目标代际与 DPAPI 凭据，并以本地确定性 key 约束至少一次重试。
/// </summary>
public sealed class ZoteroEntryExporter : IEntryExporter
{
    private const int BufferSize = 80 * 1024;
    private const int MaximumTitleLength = 1024;
    private const int MaximumCreatorLength = 512;
    private const int MaximumSummaryLength = 8 * 1024;
    private const int MaximumTagCount = 32;
    private const int MaximumTagLength = 64;
    private const int MaximumSourceUrlLength = 2048;
    private const long MaximumImageBytes = 12L * 1024 * 1024;
    private const string OfficialHost = "api.zotero.org";
    private const string ZoteroKeyAlphabet =
        "23456789ABCDEFGHIJKLMNPQRSTUVWXYZ";
    private readonly IZoteroExportTargetStore _targets;
    private readonly IEntryIntegrationPolicyService _policies;
    private readonly IEntryIntegrationCredentialStore _credentials;
    private readonly IArticleImageStreamDownloader _images;
    private readonly IZoteroApiClient _api;

    public const string ExporterId = "zotero";
    public const long MaximumContentBytes =
        MaximumImageBytes + 64L * 1024;

    public ZoteroEntryExporter(
        IZoteroExportTargetStore targets,
        IEntryIntegrationPolicyService policies,
        IEntryIntegrationCredentialStore credentials,
        IArticleImageStreamDownloader images,
        IZoteroApiClient api)
    {
        _targets = targets
            ?? throw new ArgumentNullException(nameof(targets));
        _policies = policies
            ?? throw new ArgumentNullException(nameof(policies));
        _credentials = credentials
            ?? throw new ArgumentNullException(nameof(credentials));
        _images = images
            ?? throw new ArgumentNullException(nameof(images));
        _api = api
            ?? throw new ArgumentNullException(nameof(api));
    }

    public EntryExportCapability Capability { get; } = new(
        ExporterId,
        "Zotero",
        Array.AsReadOnly(Enum.GetValues<EntryViewKind>()),
        RequiresCredentials: true,
        MaximumContentBytes,
        IsIdempotent: true);

    public async Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);

        EntryIntegrationPolicySnapshot policySnapshot =
            await GetActivePolicyAsync(cancellationToken)
                .ConfigureAwait(false);
        EntryIntegrationPolicy? policy = policySnapshot.Policies
            .SingleOrDefault(item =>
                item.Kind == EntryIntegrationKind.Zotero);
        if (policy is null
            || !policy.IsEnabled
            || !policy.AllowedHosts.Contains(
                OfficialHost,
                StringComparer.Ordinal))
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }

        // 目标租约持有到最后一次 API 调用完成；同进程保存下一代配置会等待本任务退出。
        await using IZoteroExportTargetLease lease =
            await AcquireTargetLeaseAsync(cancellationToken)
                .ConfigureAwait(false);
        ZoteroExportTarget target = NormalizeCurrentTarget(lease.Target);
        if (!target.MatchesQueueTargetId(request.TargetId))
        {
            throw Failure(EntryExportErrorCode.Conflict);
        }

        string apiKey = await GetCredentialAsync(cancellationToken)
            .ConfigureAwait(false);
        Uri sourceUrl = NormalizeSourceUrl(request.Entry.NormalizedUrl)
            ?? throw Failure(EntryExportErrorCode.UnsupportedContent);
        var apiTarget = new ZoteroApiTarget(
            target.ApiRoot,
            target.IncludeSummaryNote,
            target.UploadFirstImageAttachment);

        ZoteroApiCapability capability = await ProbeAsync(
                apiTarget,
                apiKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (capability.UserId != target.UserId
            || !capability.CanWrite
            || (target.IncludeSummaryNote
                && !capability.CanWriteNotes)
            || (target.UploadFirstImageAttachment
                && !capability.CanWriteFiles))
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }

        PreparedAttachment? attachment =
            await PrepareAttachmentAsync(
                    request,
                    target,
                    cancellationToken)
                .ConfigureAwait(false);
        ReadOnlyCollection<ZoteroItem> items = CreateItems(
            request,
            target,
            sourceUrl,
            attachment);
        IReadOnlyList<string> created = await CreateAsync(
                apiTarget,
                apiKey,
                items,
                cancellationToken)
            .ConfigureAwait(false);
        if (created.Count != items.Count
            || !created.SequenceEqual(
                items.Select(item => item.Key),
                StringComparer.Ordinal))
        {
            throw Failure(EntryExportErrorCode.ProviderRejected);
        }

        if (attachment is not null)
        {
            await UploadAttachmentAsync(
                    apiTarget,
                    apiKey,
                    attachment,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return EntryExportResult.Success(
            request.IdempotencyKey,
            items[0].Key,
            remoteUrl: null);
    }

    private async Task<EntryIntegrationPolicySnapshot>
        GetActivePolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _policies.GetAsync(
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
        catch (UnauthorizedAccessException)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        catch (Exception exception)
            when (exception is IOException
                  or HttpRequestException
                  or InvalidOperationException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
    }

    private async Task<IZoteroExportTargetLease> AcquireTargetLeaseAsync(
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
        catch (UnauthorizedAccessException)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        catch (Exception exception)
            when (exception is IOException
                  or InvalidOperationException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
        catch (ArgumentException)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
    }

    private async Task<string> GetCredentialAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            string? value = await _credentials.GetAsync(
                    EntryIntegrationKind.Zotero,
                    ZoteroExportTarget.DefaultTargetId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw Failure(
                    EntryExportErrorCode.CredentialsRequired);
            }
            return value.Trim();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntryExportException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        catch (Exception exception)
            when (exception is IOException
                  or InvalidOperationException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
    }

    private async Task<ZoteroApiCapability> ProbeAsync(
        ZoteroApiTarget target,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _api.ProbeAsync(
                    target,
                    apiKey,
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
        catch (ZoteroApiException exception)
        {
            throw MapApiFailure(exception, cancellationToken);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException)
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

    private async Task<IReadOnlyList<string>> CreateAsync(
        ZoteroApiTarget target,
        string apiKey,
        IReadOnlyList<ZoteroItem> items,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _api.CreateAsync(
                    target,
                    apiKey,
                    items,
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
        catch (ZoteroApiException exception)
        {
            throw MapApiFailure(exception, cancellationToken);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException)
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

    private async Task UploadAttachmentAsync(
        ZoteroApiTarget target,
        string apiKey,
        PreparedAttachment attachment,
        CancellationToken cancellationToken)
    {
        try
        {
            await _api.UploadAttachmentAsync(
                    target,
                    apiKey,
                    new(
                        attachment.Key,
                        attachment.FileName,
                        attachment.MimeType,
                        attachment.Bytes,
                        attachment.ModifiedTimeMilliseconds),
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
        catch (ZoteroApiException exception)
        {
            throw MapApiFailure(exception, cancellationToken);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException)
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

    private async Task<PreparedAttachment?> PrepareAttachmentAsync(
        EntryExportRequest request,
        ZoteroExportTarget target,
        CancellationToken cancellationToken)
    {
        if (!target.UploadFirstImageAttachment)
        {
            return null;
        }

        // 与 Eagle 共用首版可信图片候选边界；这里只接收 URL 允许、声明类型已验证、
        // 扩展名与 12 MiB 声明上限均通过的首张图片。
        FeedAttachmentClassification? candidate =
            EagleEntryExporter.SelectSupportedAttachment(request.Entry);
        if (candidate is null)
        {
            return null;
        }

        try
        {
            var budget = new ArticleImageDownloadBudget(
                maximumResources: 1,
                maximumNetworkBytes: MaximumImageBytes);
            await using ArticleImageStreamContent? content =
                await _images.OpenAsync(
                        request.Entry.Id,
                        candidate.SafeUrl!,
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

            string mimeType = NormalizeImageMimeType(content.MimeType)
                ?? throw Failure(
                    EntryExportErrorCode.UnsupportedContent);
            string? declaredMime = NormalizeImageMimeType(
                candidate.NormalizedMediaType);
            if (!string.Equals(
                    mimeType,
                    declaredMime,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    EntryExportErrorCode.UnsupportedContent);
            }

            byte[] bytes = await ReadBoundedAsync(
                    content.Stream,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesMimeType(mimeType, bytes))
            {
                throw Failure(
                    EntryExportErrorCode.UnsupportedContent);
            }

            string key = CreateObjectKey(
                request.IdempotencyKey,
                "attachment");
            DateTimeOffset modified = request.Entry.PublishedAt
                                      ?? request.Entry.UpdatedAt
                                      ?? DateTimeOffset.UnixEpoch;
            return new(
                key,
                $"LT{key}.{GetImageExtension(mimeType)}",
                mimeType,
                bytes,
                modified.ToUnixTimeMilliseconds());
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
        catch (Exception exception)
            when (exception is HttpRequestException or IOException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
    }

    private static ReadOnlyCollection<ZoteroItem> CreateItems(
        EntryExportRequest request,
        ZoteroExportTarget target,
        Uri sourceUrl,
        PreparedAttachment? attachment)
    {
        string parentKey = CreateObjectKey(
            request.IdempotencyKey,
            "parent");
        string? author = NormalizeText(
            request.Entry.Author,
            MaximumCreatorLength);
        string[] tags = request.Entry.Categories
            .Select(category => NormalizeText(
                category,
                MaximumTagLength))
            .Where(category => category is not null)
            .Select(category => category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTagCount)
            .ToArray();
        DateTimeOffset? itemDate =
            request.Entry.PublishedAt ?? request.Entry.UpdatedAt;
        var parent = new ZoteroItem(
            parentKey,
            target.ItemType == ZoteroItemType.Webpage
                ? "webpage"
                : "journalArticle",
            NormalizeText(
                    request.Entry.Title,
                    MaximumTitleLength)
                ?? "无标题条目",
            sourceUrl.AbsoluteUri,
            ParentItem: null,
            itemDate?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            ContainerTitle: null,
            NoteHtml: null,
            CreateMarker(request.IdempotencyKey, "parent"),
            author is null
                ? Array.Empty<ZoteroCreator>()
                : Array.AsReadOnly([new ZoteroCreator(author)]),
            Array.AsReadOnly(tags));
        var items = new List<ZoteroItem>(3) { parent };

        string? summary = target.IncludeSummaryNote
            ? NormalizeText(
                request.Entry.Summary,
                MaximumSummaryLength)
            : null;
        if (summary is not null)
        {
            items.Add(new(
                CreateObjectKey(request.IdempotencyKey, "note"),
                "note",
                Title: string.Empty,
                Url: string.Empty,
                parentKey,
                Date: null,
                ContainerTitle: null,
                NoteHtml: $"<p>{WebUtility.HtmlEncode(summary)}</p>",
                CreateMarker(request.IdempotencyKey, "note"),
                Array.Empty<ZoteroCreator>(),
                Array.Empty<string>()));
        }
        if (attachment is not null)
        {
            items.Add(new(
                attachment.Key,
                "attachment",
                Title: attachment.FileName,
                Url: string.Empty,
                parentKey,
                Date: null,
                ContainerTitle: null,
                NoteHtml: null,
                CreateMarker(request.IdempotencyKey, "attachment"),
                Array.Empty<ZoteroCreator>(),
                Array.Empty<string>(),
                attachment.MimeType,
                attachment.FileName));
        }
        return items.AsReadOnly();
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
        ReadOnlySpan<byte> bytes) => mimeType switch
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

    private static string GetImageExtension(string mimeType) =>
        mimeType switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/bmp" => "bmp",
            _ => throw new ArgumentOutOfRangeException(nameof(mimeType))
        };

    private static ZoteroExportTarget NormalizeCurrentTarget(
        ZoteroExportTarget? target)
    {
        if (target is null)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        try
        {
            ZoteroExportTarget.Validate(target);
            return target;
        }
        catch (ArgumentException)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
    }

    private static Uri? NormalizeSourceUrl(string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        if (text.Length is 0 or > MaximumSourceUrlLength
            || !Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp
                && parsed.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return null;
        }

        var builder = new UriBuilder(parsed)
        {
            Fragment = string.Empty
        };
        Uri normalized = builder.Uri;
        return normalized.AbsoluteUri.Length <= MaximumSourceUrlLength
            ? normalized
            : null;
    }

    private static string? NormalizeText(
        string? value,
        int maximumTextElements)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var result = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value)
        {
            UnicodeCategory category =
                CharUnicodeInfo.GetUnicodeCategory(character);
            if (char.IsWhiteSpace(character)
                || char.IsControl(character)
                || category == UnicodeCategory.Format)
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }

        string normalized = result.ToString().Trim();
        if (normalized.Length == 0)
        {
            return null;
        }
        var info = new StringInfo(normalized);
        return info.LengthInTextElements <= maximumTextElements
            ? normalized
            : info.SubstringByTextElements(0, maximumTextElements);
    }

    private static string CreateObjectKey(
        string idempotencyKey,
        string role)
    {
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"lenxtool:zotero:v1:{role}:{idempotencyKey}"));
        ulong value = ((ulong)digest[0] << 32)
                      | ((ulong)digest[1] << 24)
                      | ((ulong)digest[2] << 16)
                      | ((ulong)digest[3] << 8)
                      | digest[4];
        Span<char> key = stackalloc char[8];
        for (int index = 0; index < key.Length; index++)
        {
            int shift = 35 - (index * 5);
            key[index] = ZoteroKeyAlphabet[
                (int)((value >> shift) & 31)];
        }
        return new string(key);
    }

    private static string CreateMarker(
        string idempotencyKey,
        string role) =>
        $"lt:v1:{role}:{idempotencyKey}";

    private static void ValidateRequest(EntryExportRequest request)
    {
        if (!string.Equals(
                request.ExporterId,
                ExporterId,
                StringComparison.Ordinal)
            || !ZoteroExportTarget.IsSupportedQueueTargetId(
                request.TargetId)
            || request.IdempotencyKey.Length != 64
            || request.IdempotencyKey.Any(character =>
                character is not (
                    >= '0' and <= '9'
                    or >= 'a' and <= 'f'))
            || !Enum.IsDefined(request.ViewKind)
            || request.ContentBytes < 0)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        if (request.ContentBytes > MaximumContentBytes)
        {
            throw Failure(EntryExportErrorCode.ContentTooLarge);
        }
    }

    private static EntryExportException MapPolicyFailure(
        AppException exception)
    {
        EntryExportErrorCode code = exception.Error.Code
            is AppErrorCode.AccessDenied
            or AppErrorCode.CredentialsInvalid
            ? EntryExportErrorCode.AccessDenied
            : EntryExportErrorCode.DestinationUnavailable;
        return Failure(
            code,
            isRetryable: code
                == EntryExportErrorCode.DestinationUnavailable);
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
        return Failure(
            code,
            exception.Error.IsRetryable,
            code == EntryExportErrorCode.RateLimited
                ? exception.Error.RetryAfter
                : null);
    }

    private static EntryExportException MapApiFailure(
        ZoteroApiException exception,
        CancellationToken cancellationToken)
    {
        if (exception.Failure == ZoteroApiFailure.Cancelled
            && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return exception.Failure switch
        {
            ZoteroApiFailure.Unauthorized
                or ZoteroApiFailure.BlockedEndpoint =>
                Failure(EntryExportErrorCode.AccessDenied),
            ZoteroApiFailure.Conflict =>
                Failure(
                    EntryExportErrorCode.Conflict,
                    isRetryable: true),
            ZoteroApiFailure.RequestTooLarge =>
                Failure(EntryExportErrorCode.ContentTooLarge),
            ZoteroApiFailure.RateLimited =>
                Failure(
                    EntryExportErrorCode.RateLimited,
                    isRetryable: true,
                    exception.RetryAfter),
            ZoteroApiFailure.Unavailable
                or ZoteroApiFailure.Cancelled =>
                Failure(
                    EntryExportErrorCode.DestinationUnavailable,
                    isRetryable: true),
            ZoteroApiFailure.Collision =>
                Failure(EntryExportErrorCode.Conflict),
            _ => Failure(EntryExportErrorCode.ProviderRejected)
        };
    }

    private static EntryExportException Failure(
        EntryExportErrorCode code,
        bool isRetryable = false,
        TimeSpan? retryAfter = null) =>
        new(new(code, isRetryable, retryAfter));

    private sealed record PreparedAttachment(
        string Key,
        string FileName,
        string MimeType,
        byte[] Bytes,
        long ModifiedTimeMilliseconds);
}
