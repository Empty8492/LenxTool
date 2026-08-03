using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// Readwise 实际接收的纯文本摘录预览；字节数按 UTF-8 计算，供界面与耐久队列共享同一预算。
/// </summary>
public sealed record ReadwiseExcerptPreview(
    string Text,
    bool IsTruncated,
    int TextElementCount,
    int Utf8Bytes);

/// <summary>
/// 将 RSS 条目映射为 Readwise Reader Save API 文档。执行时始终重新读取 ACTIVE 策略和
/// DPAPI token，队列中只持久化固定的非秘密目标代际。
/// </summary>
public sealed class ReadwiseEntryExporter : IEntryExporter
{
    private const int MaximumExcerptTextElements = 4000;
    private const int MaximumExcerptUtf8Bytes = 16 * 1024;
    private const int MaximumTitleTextElements = 1024;
    private const int MaximumAuthorTextElements = 512;
    private const int MaximumTagTextElements = 64;
    private const int MaximumTagCount = 32;
    private const int MaximumSourceUrlLength = 2048;
    private const string OfficialHost = "readwise.io";
    private readonly IEntryIntegrationPolicyService _policies;
    private readonly IEntryIntegrationCredentialStore _credentials;
    private readonly IReadwiseApiClient _api;

    public const string ExporterId = "readwise";
    public const string CredentialTargetId = "default";
    public const string QueueTargetId = "default.v1";
    public const long MaximumContentBytes = MaximumExcerptUtf8Bytes;

    /// <summary>
    /// 固定的官方 Reader API 根地址；策略必须精确允许它的主机名。
    /// </summary>
    public static Uri ApiRoot { get; } = new(
        "https://readwise.io/",
        UriKind.Absolute);

    public ReadwiseEntryExporter(
        IEntryIntegrationPolicyService policies,
        IEntryIntegrationCredentialStore credentials,
        IReadwiseApiClient api)
    {
        _policies = policies
            ?? throw new ArgumentNullException(nameof(policies));
        _credentials = credentials
            ?? throw new ArgumentNullException(nameof(credentials));
        _api = api
            ?? throw new ArgumentNullException(nameof(api));
    }

    public EntryExportCapability Capability { get; } = new(
        ExporterId,
        "Readwise Reader",
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

        Uri sourceUrl = NormalizeSourceUrl(request.Entry.NormalizedUrl)
            ?? throw Failure(EntryExportErrorCode.UnsupportedContent);
        ReadwiseExcerptPreview preview =
            CreateExcerptPreview(request.Entry);
        if (request.ContentBytes != preview.Utf8Bytes)
        {
            // 队列声明必须与即将发送的摘录完全一致，避免过期估算绕开大小闸门。
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }

        EntryIntegrationPolicySnapshot policySnapshot =
            await GetActivePolicyAsync(cancellationToken)
                .ConfigureAwait(false);
        EntryIntegrationPolicy? policy = policySnapshot.Policies
            .FirstOrDefault(item =>
                item.Kind == EntryIntegrationKind.Readwise);
        if (policy is null
            || !policy.IsEnabled
            || !policy.AllowedHosts.Contains(
                OfficialHost,
                StringComparer.Ordinal))
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }

        string accessToken = await GetCredentialAsync(cancellationToken)
            .ConfigureAwait(false);
        ReadwiseDocument document = CreateDocument(
            request.Entry,
            sourceUrl,
            preview);
        ReadwiseSaveResult result = await SaveAsync(
                accessToken,
                document,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.Id)
            || result.Url is null
            || !result.Url.IsAbsoluteUri
            || result.Url.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(result.Url.UserInfo)
            || !string.IsNullOrEmpty(result.Url.Fragment))
        {
            throw Failure(EntryExportErrorCode.ProviderRejected);
        }

        return EntryExportResult.Success(
            request.IdempotencyKey,
            result.Id,
            result.Url);
    }

    /// <summary>
    /// 生成与实际发送完全相同的纯文本摘录；正文为空时才回退到摘要。
    /// </summary>
    public static ReadwiseExcerptPreview CreateExcerptPreview(
        FeedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string normalized = NormalizeWhitespace(entry.SanitizedContent);
        if (normalized.Length == 0)
        {
            normalized = NormalizeWhitespace(entry.Summary);
        }

        return CreateBoundedPreview(
            normalized,
            MaximumExcerptTextElements,
            MaximumExcerptUtf8Bytes);
    }

    /// <summary>
    /// 返回实际发送摘录的 UTF-8 字节数，而不是未裁剪 RSS 正文的大小。
    /// </summary>
    public static long GetExportContentBytes(FeedEntry entry) =>
        CreateExcerptPreview(entry).Utf8Bytes;

    /// <summary>
    /// 仅允许没有凭据、片段和本地目标语义的规范 HTTP(S) 公网 DNS URL。
    /// </summary>
    public static bool CanExportEntry(FeedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return NormalizeSourceUrl(entry.NormalizedUrl) is not null;
    }

    private static ReadwiseDocument CreateDocument(
        FeedEntry entry,
        Uri sourceUrl,
        ReadwiseExcerptPreview preview)
    {
        string title = NormalizeBoundedText(
                entry.Title,
                MaximumTitleTextElements)
            ?? NormalizeBoundedText(
                entry.Id,
                MaximumTitleTextElements)
            ?? "Untitled entry";
        string? author = NormalizeBoundedText(
            entry.Author,
            MaximumAuthorTextElements);
        DateTimeOffset? publishedAt =
            entry.PublishedAt ?? entry.UpdatedAt;
        string? publishedDate = publishedAt?.UtcDateTime.ToString(
            "O",
            CultureInfo.InvariantCulture);
        ReadOnlyCollection<string> tags = Array.AsReadOnly(
            entry.Categories
                .Select(category => NormalizeBoundedText(
                    category,
                    MaximumTagTextElements))
                .Where(category => category is not null)
                .Select(category => category!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumTagCount)
                .ToArray());

        return new(
            sourceUrl.AbsoluteUri,
            title,
            author,
            preview.Text.Length == 0 ? null : preview.Text,
            publishedDate,
            ImageUrl: null,
            tags,
            Notes: null);
    }

    private async Task<EntryIntegrationPolicySnapshot> GetActivePolicyAsync(
        CancellationToken cancellationToken)
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

    private async Task<string> GetCredentialAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            string? value = await _credentials.GetAsync(
                    EntryIntegrationKind.Readwise,
                    CredentialTargetId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw Failure(EntryExportErrorCode.CredentialsRequired);
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
            when (exception is IOException or InvalidOperationException)
        {
            throw Failure(
                EntryExportErrorCode.DestinationUnavailable,
                isRetryable: true);
        }
    }

    private async Task<ReadwiseSaveResult> SaveAsync(
        string accessToken,
        ReadwiseDocument document,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _api.SaveAsync(
                    accessToken,
                    document,
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
        catch (ReadwiseApiException exception)
            when (exception.Failure == ReadwiseApiFailure.Cancelled
                  && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ReadwiseApiException exception)
        {
            throw MapApiFailure(exception);
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

    private static EntryExportException MapApiFailure(
        ReadwiseApiException exception) =>
        exception.Failure switch
        {
            ReadwiseApiFailure.Unauthorized =>
                Failure(EntryExportErrorCode.AccessDenied),
            ReadwiseApiFailure.Rejected =>
                Failure(EntryExportErrorCode.ProviderRejected),
            ReadwiseApiFailure.RateLimited =>
                Failure(
                    EntryExportErrorCode.RateLimited,
                    isRetryable: true,
                    exception.RetryAfter),
            ReadwiseApiFailure.Unavailable =>
                Failure(
                    EntryExportErrorCode.DestinationUnavailable,
                    isRetryable: true,
                    exception.RetryAfter),
            ReadwiseApiFailure.UnknownWriteOutcome =>
                Failure(
                    EntryExportErrorCode.DestinationUnavailable,
                    isRetryable: true),
            ReadwiseApiFailure.BlockedEndpoint =>
                Failure(EntryExportErrorCode.AccessDenied),
            ReadwiseApiFailure.Cancelled =>
                Failure(
                    EntryExportErrorCode.DestinationUnavailable,
                    isRetryable: true),
            _ => Failure(EntryExportErrorCode.Unknown)
        };

    private static void ValidateRequest(EntryExportRequest request)
    {
        if (request.Entry is null
            || !string.Equals(
                request.ExporterId,
                ExporterId,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || !Enum.IsDefined(request.ViewKind)
            || request.ContentBytes < 0)
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }

        if (!string.Equals(
                request.TargetId,
                QueueTargetId,
                StringComparison.Ordinal))
        {
            throw Failure(EntryExportErrorCode.Conflict);
        }

        if (request.ContentBytes > MaximumExcerptUtf8Bytes)
        {
            throw Failure(EntryExportErrorCode.ContentTooLarge);
        }
    }

    private static Uri? NormalizeSourceUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumSourceUrlLength
            || !string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsoluteUri.Length > MaximumSourceUrlLength
            || string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return null;
        }

        string host = uri.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)
            || Uri.CheckHostName(host) != UriHostNameType.Dns
            || !host.Contains('.', StringComparison.Ordinal)
            || NetworkTargetClassifier.IsReservedHostName(host))
        {
            return null;
        }

        return uri;
    }

    private static string? NormalizeBoundedText(
        string? value,
        int maximumTextElements)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = NormalizeWhitespace(value);
        if (normalized.Length == 0)
        {
            return null;
        }

        return CreateBoundedPreview(
            normalized,
            maximumTextElements,
            int.MaxValue).Text;
    }

    private static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(value.Length);
        bool pendingSpace = false;
        TextElementEnumerator enumerator =
            StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            string element = enumerator.GetTextElement();
            if (IsCollapsibleWhitespace(element))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }
            normalized.Append(element);
        }

        return normalized.ToString();
    }

    private static bool IsCollapsibleWhitespace(string textElement)
    {
        bool foundRune = false;
        foreach (Rune rune in textElement.EnumerateRunes())
        {
            foundRune = true;
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (!Rune.IsWhiteSpace(rune)
                && category is not UnicodeCategory.Control
                && category is not UnicodeCategory.Format
                && category is not UnicodeCategory.LineSeparator
                && category is not UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }

        return foundRune;
    }

    private static ReadwiseExcerptPreview CreateBoundedPreview(
        string normalized,
        int maximumTextElements,
        int maximumUtf8Bytes)
    {
        var output = new StringBuilder(
            Math.Min(normalized.Length, maximumTextElements));
        int textElementCount = 0;
        int utf8Bytes = 0;
        bool isTruncated = false;
        TextElementEnumerator enumerator =
            StringInfo.GetTextElementEnumerator(normalized);
        while (enumerator.MoveNext())
        {
            string element = enumerator.GetTextElement();
            int elementBytes = Encoding.UTF8.GetByteCount(element);
            if (textElementCount >= maximumTextElements
                || elementBytes > maximumUtf8Bytes - utf8Bytes)
            {
                isTruncated = true;
                break;
            }

            output.Append(element);
            textElementCount++;
            utf8Bytes += elementBytes;
        }

        return new(
            output.ToString(),
            isTruncated,
            textElementCount,
            utf8Bytes);
    }

    private static EntryExportException Failure(
        EntryExportErrorCode code,
        bool isRetryable = false,
        TimeSpan? retryAfter = null) =>
        new(new(code, isRetryable, retryAfter));
}
