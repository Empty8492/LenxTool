using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// Zotero Web API v3 个人库目标。Endpoint 必须是官方个人库根地址，权限开关只描述本次目标实际需要的能力。
/// </summary>
public sealed record ZoteroApiTarget(
    Uri Endpoint,
    bool RequireNotesPermission,
    bool RequireFilesPermission);

/// <summary>
/// RSS 作者通常只有一个不可可靠拆分的显示名，因此使用 Zotero 官方 single-field creator，绝不猜测姓与名。
/// </summary>
public sealed record ZoteroCreator(string Name);

/// <summary>
/// exporter 与 API 边界之间的小型条目 DTO；仅支持 P2-13 的 webpage、journalArticle 与子 note。
/// </summary>
public sealed record ZoteroItem(
    string Key,
    string ItemType,
    string Title,
    string Url,
    string? ParentItem,
    string? Date,
    string? ContainerTitle,
    string? NoteHtml,
    string LenxToolMarker,
    IReadOnlyList<ZoteroCreator> Creators,
    IReadOnlyList<string> Tags,
    string? ContentType = null,
    string? FileName = null);

/// <summary>
/// 已在本机完成读取和大小约束的单个 Zotero imported_file；Content 会在进入异步网络阶段前复制。
/// </summary>
public sealed record ZoteroAttachmentUpload(
    string ItemKey,
    string FileName,
    string ContentType,
    ReadOnlyMemory<byte> Content,
    long ModifiedTimeMilliseconds);

/// <summary>
/// 只返回 exporter 和健康检查真正需要的权限快照，不携带用户名、库名或凭据。
/// </summary>
public sealed record ZoteroApiCapability(
    long UserId,
    bool CanWrite,
    bool CanWriteNotes,
    bool CanWriteFiles);

public enum ZoteroApiFailure
{
    Unauthorized = 1,
    BlockedEndpoint = 2,
    Conflict = 3,
    RequestTooLarge = 4,
    RateLimited = 5,
    Unavailable = 6,
    Rejected = 7,
    Collision = 8,
    Cancelled = 9
}

/// <summary>
/// 封闭 Zotero 失败：异常文本不包含响应正文、请求 URL、item key 或 API key。
/// </summary>
public sealed class ZoteroApiException : Exception
{
    public ZoteroApiException(
        ZoteroApiFailure failure,
        bool isRetryable,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(CreateMessage(failure), innerException)
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }
        Failure = failure;
        IsRetryable = isRetryable;
        RetryAfter = retryAfter;
    }

    public ZoteroApiFailure Failure { get; }
    public bool IsRetryable { get; }
    public TimeSpan? RetryAfter { get; }

    private static string CreateMessage(ZoteroApiFailure failure) =>
        failure switch
        {
            ZoteroApiFailure.Unauthorized =>
                "Zotero 凭据或个人库权限不满足当前导出目标。",
            ZoteroApiFailure.BlockedEndpoint =>
                "Zotero 网络目标未通过安全校验。",
            ZoteroApiFailure.Conflict =>
                "Zotero 个人库当前存在写入冲突。",
            ZoteroApiFailure.RequestTooLarge =>
                "Zotero 拒绝了过大的写入请求。",
            ZoteroApiFailure.RateLimited =>
                "Zotero 暂时限制了请求速率。",
            ZoteroApiFailure.Unavailable =>
                "Zotero 服务暂时不可用。",
            ZoteroApiFailure.Rejected =>
                "Zotero 拒绝了请求或返回了不兼容的数据。",
            ZoteroApiFailure.Collision =>
                "Zotero 中的确定性条目 key 已被其他内容占用。",
            ZoteroApiFailure.Cancelled =>
                "Zotero 请求已取消。",
            _ => "Zotero 请求失败。"
        };
}

public interface IZoteroApiClient
{
    Task<ZoteroApiCapability> ProbeAsync(
        ZoteroApiTarget target,
        string apiKey,
        CancellationToken cancellationToken);

    Task<ZoteroApiCapability> ProbePinnedAsync(
        ZoteroApiTarget target,
        string apiKey,
        IReadOnlyList<IPAddress> pinnedAddresses,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> CreateAsync(
        ZoteroApiTarget target,
        string apiKey,
        IReadOnlyList<ZoteroItem> items,
        CancellationToken cancellationToken);

    Task UploadAttachmentAsync(
        ZoteroApiTarget target,
        string apiKey,
        ZoteroAttachmentUpload upload,
        CancellationToken cancellationToken);
}

internal interface IZoteroHttpClientFactory
{
    HttpClient Create(
        Uri endpoint,
        IReadOnlyList<IPAddress> pinnedAddresses);
}

internal interface IZoteroClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken);
}

/// <summary>
/// Zotero 专用 handler 复用项目的固定地址连接工厂，并进一步把每个目标的 HTTP 并发限制为一。
/// </summary>
internal static class ZoteroHttpClientSecurity
{
    public static SocketsHttpHandler CreatePrimaryHandler(
        Uri endpoint,
        IReadOnlyList<IPAddress> pinnedAddresses)
    {
        SocketsHttpHandler handler = PinnedHttpHandlerFactory.Create(
            endpoint,
            pinnedAddresses,
            TimeSpan.FromSeconds(5),
            DecompressionMethods.None);
        handler.MaxConnectionsPerServer = 1;
        return handler;
    }
}

internal sealed class PinnedZoteroHttpClientFactory
    : IZoteroHttpClientFactory
{
    public HttpClient Create(
        Uri endpoint,
        IReadOnlyList<IPAddress> pinnedAddresses) =>
        new(
            ZoteroHttpClientSecurity.CreatePrimaryHandler(
                endpoint,
                pinnedAddresses),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
}

internal sealed class SystemZoteroClock : IZoteroClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Zotero Web API v3 安全客户端。官方文档要求生产代码固定 v3、凭据使用请求头、
/// 新对象使用 version 0；本实现额外以预分配 key 做前后身份核对，避免重试覆盖碰撞对象。
/// 来源：https://www.zotero.org/support/dev/web_api/v3/basics 、
/// https://www.zotero.org/support/dev/web_api/v3/write_requests 、
/// https://www.zotero.org/support/dev/web_api/v3/syncing 。
/// </summary>
internal sealed class ZoteroApiClient : IZoteroApiClient
{
    private const int MaximumResponseBytes = 256 * 1024;
    private const int MaximumWriteAttempts = 2;
    private const int MaximumTitleLength = 1024;
    private const int MaximumUrlLength = 2048;
    private const int MaximumDateLength = 128;
    private const int MaximumContainerTitleLength = 512;
    private const int MaximumNoteLength = 64 * 1024;
    private const int MaximumMarkerLength = 128;
    private const int MaximumCreatorNameLength = 512;
    private const int MaximumTagCount = 64;
    private const int MaximumTagLength = 256;
    private const int MaximumApiKeyLength = 256;
    private const int MaximumFileNameLength = 255;
    private const int MaximumMimeTypeLength = 128;
    private const int MaximumUploadUrlLength = 2048;
    private const int MaximumUploadFieldLength = 64 * 1024;
    private const int MaximumUploadKeyLength = 512;
    private const int MaximumAttachmentBytes = 12 * 1024 * 1024;
    private const string OfficialHost = "api.zotero.org";
    private const string KeyAlphabet =
        "23456789ABCDEFGHIJKLMNPQRSTUVWXYZ";
    private static readonly HashSet<string> AllowedAttachmentMimeTypes =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "image/bmp"
    ];
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(8);
    // 附件最大可达 12 MiB，上传正文不能沿用短请求的 8 秒上限。
    private static readonly TimeSpan AttachmentUploadTimeout =
        TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumServerPause =
        TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 16
        };

    private readonly IFeedHostResolver _resolver;
    private readonly IZoteroHttpClientFactory _clients;
    private readonly IZoteroClock _clock;
    private readonly ConcurrentDictionary<string, TargetState> _targetStates =
        new(StringComparer.Ordinal);

    public ZoteroApiClient(IFeedHostResolver resolver)
        : this(
            resolver,
            new PinnedZoteroHttpClientFactory(),
            new SystemZoteroClock())
    {
    }

    internal ZoteroApiClient(
        IFeedHostResolver resolver,
        IZoteroHttpClientFactory clients,
        IZoteroClock clock)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<ZoteroApiCapability> ProbeAsync(
        ZoteroApiTarget target,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ValidatedTarget validated = ValidateTarget(target);
        string credential = ValidateApiKey(apiKey);
        return ExecuteAsync(
            validated,
            credential,
            pinnedAddresses: null,
            ProbeCoreAsync,
            cancellationToken);
    }

    public Task<ZoteroApiCapability> ProbePinnedAsync(
        ZoteroApiTarget target,
        string apiKey,
        IReadOnlyList<IPAddress> pinnedAddresses,
        CancellationToken cancellationToken)
    {
        ValidatedTarget validated = ValidateTarget(target);
        string credential = ValidateApiKey(apiKey);
        ArgumentNullException.ThrowIfNull(pinnedAddresses);
        return ExecuteAsync(
            validated,
            credential,
            Array.AsReadOnly(pinnedAddresses.ToArray()),
            ProbeCoreAsync,
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> CreateAsync(
        ZoteroApiTarget target,
        string apiKey,
        IReadOnlyList<ZoteroItem> items,
        CancellationToken cancellationToken)
    {
        ValidatedTarget validated = ValidateTarget(target);
        string credential = ValidateApiKey(apiKey);
        IReadOnlyList<ValidatedItem> normalized = ValidateItems(items);
        if ((!validated.RequireNotesPermission
                && normalized.Any(item => item.Value.ItemType == "note"))
            || (!validated.RequireFilesPermission
                && normalized.Any(item =>
                    item.Value.ItemType == "attachment")))
        {
            throw new ArgumentException(
                "Zotero 批次包含目标未启用的子项类型。",
                nameof(items));
        }
        return ExecuteAsync(
            validated,
            credential,
            pinnedAddresses: null,
            (context, token) => CreateCoreAsync(
                context,
                normalized,
                token),
            cancellationToken);
    }

    public Task UploadAttachmentAsync(
        ZoteroApiTarget target,
        string apiKey,
        ZoteroAttachmentUpload upload,
        CancellationToken cancellationToken)
    {
        ValidatedTarget validated = ValidateTarget(target);
        string credential = ValidateApiKey(apiKey);
        ValidatedAttachmentUpload normalized = ValidateUpload(upload);
        if (!validated.RequireFilesPermission)
        {
            throw new ArgumentException(
                "Zotero 目标未启用文件上传。",
                nameof(target));
        }
        return ExecuteAsync(
            validated,
            credential,
            pinnedAddresses: null,
            async (context, token) =>
            {
                await UploadAttachmentCoreAsync(
                    context,
                    normalized,
                    token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    internal static ValidatedTarget ValidateTarget(ZoteroApiTarget? target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Uri endpoint = target.Endpoint;
        if (!endpoint.IsAbsoluteUri
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                endpoint.IdnHost,
                OfficialHost,
                StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != 443
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw InvalidTarget();
        }

        string[] segments = endpoint.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2
            || !string.Equals(segments[0], "users", StringComparison.Ordinal)
            || !long.TryParse(
                segments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long userId)
            || userId <= 0)
        {
            throw InvalidTarget();
        }
        string canonicalPath = string.Create(
            CultureInfo.InvariantCulture,
            $"/users/{userId}/");
        if (!string.Equals(
            endpoint.AbsolutePath,
            canonicalPath,
            StringComparison.Ordinal))
        {
            throw InvalidTarget();
        }

        return new(
            new($"https://{OfficialHost}{canonicalPath}"),
            userId,
            target.RequireNotesPermission,
            target.RequireFilesPermission);
    }

    private async Task<TResult> ExecuteAsync<TResult>(
        ValidatedTarget target,
        string apiKey,
        IReadOnlyList<IPAddress>? pinnedAddresses,
        Func<RequestContext, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        TargetState state = _targetStates.GetOrAdd(
            target.Endpoint.AbsoluteUri,
            static _ => new());
        bool acquired = false;
        try
        {
            try
            {
                await state.Gate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                acquired = true;
            }
            catch (OperationCanceledException exception)
            {
                throw Cancelled(exception);
            }

            await WaitForPauseAsync(state, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<IPAddress> addresses = pinnedAddresses is null
                ? await ResolvePublicAsync(
                    target.Endpoint,
                    cancellationToken).ConfigureAwait(false)
                : ValidatePublicAddresses(pinnedAddresses);
            HttpClient client = CreatePinnedClient(
                target.Endpoint,
                addresses);
            using (client)
            {
                return await action(
                    new(target, apiKey, client, state),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception)
        {
            throw Cancelled(exception);
        }
        finally
        {
            if (acquired) state.Gate.Release();
        }
    }

    private async Task<ZoteroApiCapability> ProbeCoreAsync(
        RequestContext context,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendJsonAsync(
                context,
                HttpMethod.Get,
                new($"https://{OfficialHost}/keys/current"),
                body: null,
                allowNotFound: false,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Rejected();
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("userID", out JsonElement userIdProperty)
            || !userIdProperty.TryGetInt64(out long userId)
            || !root.TryGetProperty("access", out JsonElement access)
            || access.ValueKind != JsonValueKind.Object
            || !access.TryGetProperty("user", out JsonElement user)
            || user.ValueKind != JsonValueKind.Object
            || !TryGetBoolean(user, "library", out bool library)
            || !TryGetBoolean(user, "write", out bool write)
            || !TryGetOptionalBoolean(user, "notes", out bool notes)
            || !TryGetOptionalBoolean(user, "files", out bool files))
        {
            throw Rejected();
        }
        if (userId != context.Target.UserId
            || !library
            || !write
            || (context.Target.RequireNotesPermission && !notes)
            || (context.Target.RequireFilesPermission && !files))
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.Unauthorized,
                isRetryable: false);
        }
        return new(userId, write, notes, files);
    }

    private async Task UploadAttachmentCoreAsync(
        RequestContext context,
        ValidatedAttachmentUpload upload,
        CancellationToken cancellationToken)
    {
        Uri fileEndpoint = new(
            context.Target.Endpoint,
            $"items/{upload.ItemKey}/file");
        using JsonDocument authorization = await SendFormForJsonAsync(
                context,
                fileEndpoint,
                [
                    new("md5", upload.Md5),
                    new("filename", upload.FileName),
                    new(
                        "filesize",
                        upload.Content.Length.ToString(
                            CultureInfo.InvariantCulture)),
                    new(
                        "mtime",
                        upload.ModifiedTimeMilliseconds.ToString(
                            CultureInfo.InvariantCulture))
                ],
                cancellationToken)
            .ConfigureAwait(false);
        UploadAuthorization parsed = ParseUploadAuthorization(
            authorization.RootElement);
        if (parsed.Exists) return;

        Uri uploadUri = ValidateSignedUploadUri(parsed.UploadUri!);
        IReadOnlyList<IPAddress> uploadAddresses =
            await ResolvePublicAsync(uploadUri, cancellationToken)
                .ConfigureAwait(false);
        using HttpClient uploadClient = CreatePinnedClient(
            uploadUri,
            uploadAddresses);
        byte[] uploadBody = ConcatenateUploadBody(
            parsed.Prefix!,
            upload.Content,
            parsed.Suffix!);
        using (var request = new HttpRequestMessage(
                   HttpMethod.Post,
                   uploadUri))
        {
            request.Headers.UserAgent.Add(
                new ProductInfoHeaderValue("LenxTool", "0.1"));
            request.Content = new ByteArrayContent(uploadBody);
            request.Content.Headers.ContentType =
                parsed.UploadContentType;
            await SendForExpectedStatusAsync(
                    uploadClient,
                    context.State,
                     request,
                     HttpStatusCode.Created,
                     cancellationToken,
                     AttachmentUploadTimeout)
                .ConfigureAwait(false);
        }

        using var register = new HttpRequestMessage(
            HttpMethod.Post,
            fileEndpoint);
        AddOfficialHeaders(register, context.ApiKey);
        register.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        register.Content = new FormUrlEncodedContent(
        [
            new("upload", parsed.UploadKey!)
        ]);
        await SendForExpectedStatusAsync(
                context.Client,
                context.State,
                register,
                HttpStatusCode.NoContent,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private HttpClient CreatePinnedClient(
        Uri endpoint,
        IReadOnlyList<IPAddress> addresses)
    {
        try
        {
            return _clients.Create(endpoint, addresses);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException
                  or ArgumentException
                  or HttpRequestException
                  or IOException
                  or SocketException)
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.Unavailable,
                isRetryable: true);
        }
    }

    private async Task<IReadOnlyList<string>> CreateCoreAsync(
        RequestContext context,
        IReadOnlyList<ValidatedItem> items,
        CancellationToken cancellationToken)
    {
        string[] allKeys = items.Select(item => item.Value.Key).ToArray();
        for (int attempt = 0; attempt < MaximumWriteAttempts; attempt++)
        {
            IReadOnlyList<ValidatedItem> missing = await FindMissingAsync(
                    context,
                    items,
                    cancellationToken)
                .ConfigureAwait(false);
            if (missing.Count == 0)
            {
                return Array.AsReadOnly(allKeys);
            }

            ZoteroApiException? writeFailure = null;
            try
            {
                await PostItemsAsync(
                    context,
                    missing,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ZoteroApiException exception)
            {
                if (exception.Failure == ZoteroApiFailure.Cancelled)
                {
                    throw;
                }
                writeFailure = exception;
            }

            try
            {
                IReadOnlyList<ValidatedItem> stillMissing =
                    await FindMissingAsync(
                        context,
                        items,
                        cancellationToken).ConfigureAwait(false);
                if (stillMissing.Count == 0)
                {
                    return Array.AsReadOnly(allKeys);
                }
            }
            catch (ZoteroApiException exception)
                when (exception.Failure != ZoteroApiFailure.Collision
                      && exception.Failure != ZoteroApiFailure.Cancelled)
            {
                if (writeFailure is null)
                {
                    writeFailure = exception;
                }
            }

            if (writeFailure is not null
                && (!writeFailure.IsRetryable
                    || attempt == MaximumWriteAttempts - 1))
            {
                throw writeFailure;
            }
            if (attempt == MaximumWriteAttempts - 1)
            {
                throw writeFailure ?? new ZoteroApiException(
                    ZoteroApiFailure.Unavailable,
                    isRetryable: true);
            }

            ScheduleFallbackPause(context.State, attempt);
        }

        throw new ZoteroApiException(
            ZoteroApiFailure.Unavailable,
            isRetryable: true);
    }

    private async Task<IReadOnlyList<ValidatedItem>> FindMissingAsync(
        RequestContext context,
        IReadOnlyList<ValidatedItem> items,
        CancellationToken cancellationToken)
    {
        var missing = new List<ValidatedItem>();
        foreach (ValidatedItem item in items)
        {
            bool exists = await ExistsMatchingAsync(
                    context,
                    item,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!exists) missing.Add(item);
        }
        return missing.AsReadOnly();
    }

    private async Task<bool> ExistsMatchingAsync(
        RequestContext context,
        ValidatedItem expected,
        CancellationToken cancellationToken)
    {
        Uri uri = new(
            context.Target.Endpoint,
            $"items/{expected.Value.Key}");
        using JsonDocument? document = await SendJsonAsync(
                context,
                HttpMethod.Get,
                uri,
                body: null,
                allowNotFound: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (document is null) return false;

        if (!MatchesIdentity(document.RootElement, expected))
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.Collision,
                isRetryable: false);
        }
        return true;
    }

    private async Task PostItemsAsync(
        RequestContext context,
        IReadOnlyList<ValidatedItem> items,
        CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            items.Select(CreateWriteObject).ToArray(),
            JsonOptions);
        using JsonDocument document = await SendJsonAsync(
                context,
                HttpMethod.Post,
                new(context.Target.Endpoint, "items"),
                body,
                allowNotFound: false,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Rejected();
        ValidateWriteResponse(document.RootElement, items.Count);
    }

    private async Task<JsonDocument?> SendJsonAsync(
        RequestContext context,
        HttpMethod method,
        Uri uri,
        byte[]? body,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        AddOfficialHeaders(request, context.ApiKey);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json")
            {
                CharSet = "utf-8"
            };
        }
        return await SendJsonRequestAsync(
                context,
                request,
                allowNotFound,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendFormForJsonAsync(
        RequestContext context,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        AddOfficialHeaders(request, context.ApiKey);
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        request.Content = new FormUrlEncodedContent(fields);
        return await SendJsonRequestAsync(
                context,
                request,
                allowNotFound: false,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Rejected();
    }

    private async Task<JsonDocument?> SendJsonRequestAsync(
        RequestContext context,
        HttpRequestMessage request,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        await WaitForPauseAsync(context.State, cancellationToken)
            .ConfigureAwait(false);

        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using HttpResponseMessage response = await context.Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            TimeSpan? retryAfter = UpdateServerPause(
                response,
                context.State);
            if (allowNotFound
                && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw MapStatus(response.StatusCode, retryAfter);
            }
            return await ReadJsonAsync(
                    response.Content,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (ZoteroApiException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw Cancelled(exception);
            }
            throw new ZoteroApiException(
                ZoteroApiFailure.Unavailable,
                isRetryable: true);
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                  or IOException
                  or SocketException)
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.Unavailable,
                isRetryable: true);
        }
    }

    private async Task SendForExpectedStatusAsync(
        HttpClient client,
        TargetState state,
        HttpRequestMessage request,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        await WaitForPauseAsync(state, cancellationToken)
            .ConfigureAwait(false);
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout ?? RequestTimeout);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            TimeSpan? retryAfter = UpdateServerPause(response, state);
            if (response.Content.Headers.ContentLength
                is > MaximumResponseBytes)
            {
                throw Rejected();
            }
            if (response.StatusCode != expectedStatus)
            {
                throw response.IsSuccessStatusCode
                    ? Rejected()
                    : MapStatus(response.StatusCode, retryAfter);
            }
        }
        catch (ZoteroApiException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw Cancelled(exception);
            }
            throw new ZoteroApiException(
                ZoteroApiFailure.Unavailable,
                isRetryable: true);
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                  or IOException
                  or SocketException)
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.Unavailable,
                isRetryable: true);
        }
    }

    private static void AddOfficialHeaders(
        HttpRequestMessage request,
        string apiKey)
    {
        request.Headers.TryAddWithoutValidation(
            "Zotero-API-Version",
            "3");
        request.Headers.TryAddWithoutValidation(
            "Zotero-API-Key",
            apiKey);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue("LenxTool", "0.1"));
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw Rejected();
        }
        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] json = await ReadBoundedAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    MaxDepth = JsonOptions.MaxDepth,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
        }
        catch (JsonException)
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.Rejected,
                isRetryable: false);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
            {
                throw Rejected();
            }
            output.Write(buffer, 0, read);
        }
    }

    private static UploadAuthorization ParseUploadAuthorization(
        JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Rejected();
        }
        if (root.TryGetProperty("exists", out JsonElement exists))
        {
            if (exists.ValueKind == JsonValueKind.Number
                && exists.TryGetInt32(out int value)
                && value == 1)
            {
                return UploadAuthorization.Existing;
            }
            throw Rejected();
        }
        if (!TryGetString(root, "url", out string uploadUrl)
            || !TryGetString(
                root,
                "contentType",
                out string contentType)
            || !TryGetString(root, "prefix", out string prefix)
            || !TryGetString(root, "suffix", out string suffix)
            || !TryGetString(root, "uploadKey", out string uploadKey)
            || uploadUrl.Length is 0 or > MaximumUploadUrlLength
            || contentType.Length is 0 or > MaximumMimeTypeLength
            || Encoding.UTF8.GetByteCount(prefix)
                > MaximumUploadFieldLength
            || Encoding.UTF8.GetByteCount(suffix)
                > MaximumUploadFieldLength
            || uploadKey.Length is 0 or > MaximumUploadKeyLength
            || uploadKey.Any(character => character is < '!' or > '~')
            || !MediaTypeHeaderValue.TryParse(
                contentType,
                out MediaTypeHeaderValue? parsedContentType)
            || !string.Equals(
                parsedContentType.MediaType,
                "multipart/form-data",
                StringComparison.OrdinalIgnoreCase)
            || !parsedContentType.Parameters.Any(parameter =>
                string.Equals(
                    parameter.Name,
                    "boundary",
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(parameter.Value)))
        {
            throw Rejected();
        }
        return new(
            Exists: false,
            uploadUrl,
            parsedContentType,
            prefix,
            suffix,
            uploadKey);
    }

    private static Uri ValidateSignedUploadUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.AbsoluteUri.Length > MaximumUploadUrlLength
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(uri.IdnHost)
            || IPAddress.TryParse(uri.IdnHost, out _)
            || NetworkTargetClassifier.IsReservedHostName(uri.IdnHost))
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.BlockedEndpoint,
                isRetryable: false);
        }
        return uri;
    }

    private static byte[] ConcatenateUploadBody(
        string prefix,
        byte[] content,
        string suffix)
    {
        byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix);
        byte[] suffixBytes = Encoding.UTF8.GetBytes(suffix);
        var result = new byte[
            prefixBytes.Length + content.Length + suffixBytes.Length];
        Buffer.BlockCopy(
            prefixBytes,
            0,
            result,
            0,
            prefixBytes.Length);
        Buffer.BlockCopy(
            content,
            0,
            result,
            prefixBytes.Length,
            content.Length);
        Buffer.BlockCopy(
            suffixBytes,
            0,
            result,
            prefixBytes.Length + content.Length,
            suffixBytes.Length);
        return result;
    }

    private async Task<IReadOnlyList<IPAddress>> ResolvePublicAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<IPAddress> addresses = await _resolver.ResolveAsync(
                    endpoint.IdnHost,
                    cancellationToken)
                .ConfigureAwait(false);
            return ValidatePublicAddresses(addresses);
        }
        catch (ZoteroApiException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw Cancelled(exception);
        }
        catch (Exception exception)
            when (exception is SocketException
                  or ArgumentException
                  or HttpRequestException)
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.Unavailable,
                isRetryable: true);
        }
    }

    private static ReadOnlyCollection<IPAddress> ValidatePublicAddresses(
        IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Any(address => address is null))
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.BlockedEndpoint,
                isRetryable: false);
        }
        IPAddress[] distinct = addresses.Distinct().ToArray();
        if (distinct.Length == 0
            || distinct.Any(address =>
                NetworkTargetClassifier.Classify(address)
                    is NetworkAddressDisposition.Private
                    or NetworkAddressDisposition.Forbidden))
        {
            throw new ZoteroApiException(
                ZoteroApiFailure.BlockedEndpoint,
                isRetryable: false);
        }
        return Array.AsReadOnly(distinct);
    }

    private async Task WaitForPauseAsync(
        TargetState state,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = state.PauseUntil - _clock.UtcNow;
        if (remaining <= TimeSpan.Zero) return;
        try
        {
            await _clock.DelayAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw Cancelled(exception);
        }
    }

    private TimeSpan? UpdateServerPause(
        HttpResponseMessage response,
        TargetState state)
    {
        TimeSpan? backoff = ParseBackoff(response);
        TimeSpan? retryAfter = response.StatusCode is
                HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable
            ? ParseRetryAfter(response)
            : null;
        TimeSpan? pause = Max(backoff, retryAfter);
        if (pause is { } value)
        {
            DateTimeOffset candidate = _clock.UtcNow + value;
            if (candidate > state.PauseUntil)
            {
                state.PauseUntil = candidate;
            }
        }
        return retryAfter;
    }

    private static TimeSpan? ParseBackoff(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(
                "Backoff",
                out IEnumerable<string>? values))
        {
            return null;
        }
        string? raw = values.FirstOrDefault();
        return int.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int seconds)
            ? BoundPause(TimeSpan.FromSeconds(seconds))
            : null;
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? value = response.Headers.RetryAfter;
        if (value?.Delta is { } delta)
        {
            return BoundPause(delta);
        }
        if (value?.Date is { } date)
        {
            return BoundPause(date - _clock.UtcNow);
        }
        return null;
    }

    private static TimeSpan BoundPause(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        return value > MaximumServerPause
            ? MaximumServerPause
            : value;
    }

    private void ScheduleFallbackPause(TargetState state, int attempt)
    {
        TimeSpan delay = TimeSpan.FromSeconds(1 << attempt);
        DateTimeOffset candidate = _clock.UtcNow + delay;
        if (candidate > state.PauseUntil)
        {
            state.PauseUntil = candidate;
        }
    }

    private static TimeSpan? Max(TimeSpan? first, TimeSpan? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return first > second ? first : second;
    }

    private static ZoteroApiException MapStatus(
        HttpStatusCode status,
        TimeSpan? retryAfter) =>
        status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new(
                    ZoteroApiFailure.Unauthorized,
                    isRetryable: false),
            HttpStatusCode.Conflict =>
                new(
                    ZoteroApiFailure.Conflict,
                    isRetryable: true),
            HttpStatusCode.PreconditionFailed
                or (HttpStatusCode)428 =>
                new(
                    ZoteroApiFailure.Conflict,
                    isRetryable: false),
            HttpStatusCode.RequestEntityTooLarge =>
                new(
                    ZoteroApiFailure.RequestTooLarge,
                    isRetryable: false),
            HttpStatusCode.TooManyRequests =>
                new(
                    ZoteroApiFailure.RateLimited,
                    isRetryable: true,
                    retryAfter),
            HttpStatusCode.RequestTimeout =>
                new(
                    ZoteroApiFailure.Unavailable,
                    isRetryable: true),
            >= HttpStatusCode.InternalServerError =>
                new(
                    ZoteroApiFailure.Unavailable,
                    isRetryable: true,
                    retryAfter),
            _ => Rejected()
        };

    private static void ValidateWriteResponse(
        JsonElement root,
        int itemCount)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Rejected();
        }
        if (root.TryGetProperty("failed", out JsonElement failed))
        {
            if (failed.ValueKind != JsonValueKind.Object)
            {
                throw Rejected();
            }
            JsonProperty? firstFailure = failed.EnumerateObject()
                .Cast<JsonProperty?>()
                .FirstOrDefault();
            if (firstFailure is { } failure)
            {
                int code = failure.Value.ValueKind == JsonValueKind.Object
                           && failure.Value.TryGetProperty(
                               "code",
                               out JsonElement codeProperty)
                           && codeProperty.TryGetInt32(out int parsed)
                    ? parsed
                    : 400;
                throw MapStatus((HttpStatusCode)code, retryAfter: null);
            }
        }

        JsonElement successful;
        if (!root.TryGetProperty("successful", out successful)
            && !root.TryGetProperty("success", out successful))
        {
            throw Rejected();
        }
        if (successful.ValueKind != JsonValueKind.Object)
        {
            throw Rejected();
        }
        JsonElement unchanged = default;
        bool hasUnchanged = root.TryGetProperty(
            "unchanged",
            out unchanged);
        if (hasUnchanged && unchanged.ValueKind != JsonValueKind.Object)
        {
            throw Rejected();
        }
        for (int index = 0; index < itemCount; index++)
        {
            string name = index.ToString(CultureInfo.InvariantCulture);
            if (!successful.TryGetProperty(name, out _)
                && !(hasUnchanged
                     && unchanged.TryGetProperty(name, out _)))
            {
                throw Rejected();
            }
        }
    }

    private static bool MatchesIdentity(
        JsonElement root,
        ValidatedItem expected)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object
            || !TryGetString(data, "key", out string key)
            || !string.Equals(
                key,
                expected.Value.Key,
                StringComparison.Ordinal)
            || !TryGetString(data, "itemType", out string itemType)
            || !string.Equals(
                itemType,
                expected.Value.ItemType,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryGetOptionalString(data, "url", allowFalse: false, out string? url)
            || !TryGetOptionalString(
                data,
                "parentItem",
                allowFalse: true,
                out string? actualParent))
        {
            return false;
        }
        string actualUrl = url ?? string.Empty;
        actualParent = string.IsNullOrEmpty(actualParent)
            ? null
            : actualParent;
        if (!string.Equals(
                actualUrl,
                expected.Value.Url,
                StringComparison.Ordinal)
            || !string.Equals(
                actualParent,
                expected.Value.ParentItem,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (expected.Value.ItemType == "attachment"
            && (!TryGetString(data, "linkMode", out string linkMode)
                || !string.Equals(
                    linkMode,
                    "imported_file",
                    StringComparison.Ordinal)
                || !TryGetString(
                    data,
                    "contentType",
                    out string contentType)
                || !string.Equals(
                    contentType,
                    expected.Value.ContentType,
                    StringComparison.Ordinal)
                || !TryGetString(data, "filename", out string filename)
                || !string.Equals(
                    filename,
                    expected.Value.FileName,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        string fieldName = expected.Value.ItemType is "note" or "attachment"
            ? "note"
            : "extra";
        if (!TryGetOptionalString(
                data,
                fieldName,
                allowFalse: false,
                out string? markerContainer)
            || markerContainer is null)
        {
            return false;
        }
        if (expected.Value.ItemType is "note" or "attachment")
        {
            return markerContainer.Contains(
                expected.MarkerToken,
                StringComparison.Ordinal);
        }
        return markerContainer
            .Split('\n')
            .Any(line => string.Equals(
                line.TrimEnd('\r'),
                expected.MarkerToken,
                StringComparison.Ordinal));
    }

    private static object CreateWriteObject(ValidatedItem item)
    {
        ZoteroItem value = item.Value;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = value.Key,
            ["version"] = 0,
            ["itemType"] = value.ItemType,
            ["tags"] = value.Tags.Select(tag => new { tag }).ToArray(),
            ["collections"] = Array.Empty<string>(),
            ["relations"] = new Dictionary<string, string>()
        };
        if (value.ItemType == "note")
        {
            result["parentItem"] = value.ParentItem;
            result["note"] = $"{value.NoteHtml}\n{item.MarkerToken}";
            return result;
        }
        if (value.ItemType == "attachment")
        {
            result["parentItem"] = value.ParentItem;
            result["linkMode"] = "imported_file";
            result["title"] = value.Title;
            result["note"] = item.MarkerToken;
            result["contentType"] = value.ContentType;
            result["charset"] = string.Empty;
            result["filename"] = value.FileName;
            return result;
        }

        result["title"] = value.Title;
        result["url"] = value.Url;
        result["creators"] = value.Creators
            .Select(creator => new
            {
                creatorType = "author",
                name = creator.Name
            })
            .ToArray();
        result["date"] = value.Date;
        result["extra"] = item.MarkerToken;
        if (!string.IsNullOrEmpty(value.ContainerTitle))
        {
            result[value.ItemType == "journalArticle"
                ? "publicationTitle"
                : "websiteTitle"] = value.ContainerTitle;
        }
        return result;
    }

    private static ReadOnlyCollection<ValidatedItem> ValidateItems(
        IReadOnlyList<ZoteroItem>? items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 1 or > 3)
        {
            throw new ArgumentException(
                "Zotero 批次必须包含一个父条目、至多一个子笔记和至多一个文件附件。",
                nameof(items));
        }
        var result = new List<ValidatedItem>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            ZoteroItem item = items[index]
                ?? throw new ArgumentException(
                    "Zotero 条目不能为空。",
                    nameof(items));
            result.Add(ValidateItem(item, index, result));
        }
        if (result.Count(item => item.Value.ItemType == "note") > 1
            || result.Count(item => item.Value.ItemType == "attachment") > 1)
        {
            throw new ArgumentException(
                "Zotero 批次不能包含重复的子笔记或文件附件。",
                nameof(items));
        }
        return result.AsReadOnly();
    }

    private static ValidatedItem ValidateItem(
        ZoteroItem item,
        int index,
        IReadOnlyList<ValidatedItem> preceding)
    {
        ValidateKey(item.Key, nameof(item));
        string marker = item.LenxToolMarker;
        if (string.IsNullOrWhiteSpace(marker)
            || marker.Length > MaximumMarkerLength
            || !string.Equals(marker, marker.Trim(), StringComparison.Ordinal)
            || marker.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException(
                "Zotero LenxTool marker 无效。",
                nameof(item));
        }
        ArgumentNullException.ThrowIfNull(item.Creators);
        ArgumentNullException.ThrowIfNull(item.Tags);
        if (item.Tags.Count > MaximumTagCount
            || item.Tags.Any(tag =>
                string.IsNullOrWhiteSpace(tag)
                || tag.Length > MaximumTagLength
                || tag.Any(char.IsControl)
                || !string.Equals(tag, tag.Trim(), StringComparison.Ordinal))
            || item.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != item.Tags.Count)
        {
            throw new ArgumentException(
                "Zotero 标签无效。",
                nameof(item));
        }

        if (item.ItemType == "note")
        {
            if (index == 0
                || preceding.Count < 1
                || !string.Equals(
                    item.ParentItem,
                    preceding[0].Value.Key,
                    StringComparison.Ordinal)
                || item.Title.Length != 0
                || item.Url.Length != 0
                || item.Date is not null
                || item.ContainerTitle is not null
                || item.ContentType is not null
                || item.FileName is not null
                || string.IsNullOrWhiteSpace(item.NoteHtml)
                || item.NoteHtml.Length > MaximumNoteLength
                || item.Creators.Count != 0)
            {
                throw new ArgumentException(
                    "Zotero 子笔记必须紧跟父条目并只携带笔记字段。",
                    nameof(item));
            }
            return new(
                item with
                {
                    Tags = Array.AsReadOnly(item.Tags.ToArray())
                },
                $"<!-- LenxTool-Marker:{marker} -->");
        }

        if (item.ItemType == "attachment")
        {
            if (index == 0
                || preceding.Count < 1
                || !string.Equals(
                    item.ParentItem,
                    preceding[0].Value.Key,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(item.Title)
                || item.Title.Length > MaximumTitleLength
                || item.Title.Any(char.IsControl)
                || !string.Equals(
                    item.Title,
                    item.Title.Trim(),
                    StringComparison.Ordinal)
                || item.Url.Length != 0
                || item.Date is not null
                || item.ContainerTitle is not null
                || item.NoteHtml is not null
                || item.Creators.Count != 0
                || !ValidateMimeType(item.ContentType)
                || !ValidateFileName(item.FileName))
            {
                throw new ArgumentException(
                    "Zotero imported_file 附件字段无效。",
                    nameof(item));
            }
            return new(
                item with
                {
                    Tags = Array.AsReadOnly(item.Tags.ToArray())
                },
                $"<!-- LenxTool-Marker:{marker} -->");
        }

        if (index != 0
            || item.ItemType is not ("webpage" or "journalArticle")
            || item.ParentItem is not null
            || item.NoteHtml is not null
            || item.ContentType is not null
            || item.FileName is not null
            || string.IsNullOrWhiteSpace(item.Title)
            || item.Title.Length > MaximumTitleLength
            || item.Title.Any(char.IsControl)
            || !string.Equals(item.Title, item.Title.Trim(), StringComparison.Ordinal)
            || !TryValidateSourceUrl(item.Url)
            || !ValidateOptionalText(item.Date, MaximumDateLength)
            || !ValidateOptionalText(
                item.ContainerTitle,
                MaximumContainerTitleLength)
            || item.Creators.Any(creator =>
                creator is null
                || string.IsNullOrWhiteSpace(creator.Name)
                || creator.Name.Length > MaximumCreatorNameLength
                || creator.Name.Any(char.IsControl)
                || !string.Equals(
                    creator.Name,
                    creator.Name.Trim(),
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Zotero 父条目字段无效。",
                nameof(item));
        }
        return new(
            item with
            {
                Creators = Array.AsReadOnly(item.Creators.ToArray()),
                Tags = Array.AsReadOnly(item.Tags.ToArray())
            },
            $"LenxTool-Marker: {marker}");
    }

    private static bool TryValidateSourceUrl(string value) =>
        value.Length <= MaximumUrlLength
        && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp
            || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo);

    private static ValidatedAttachmentUpload ValidateUpload(
        ZoteroAttachmentUpload? upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ValidateKey(upload.ItemKey, nameof(upload));
        if (!ValidateFileName(upload.FileName)
            || !ValidateMimeType(upload.ContentType)
            || upload.Content.Length is < 1 or > MaximumAttachmentBytes
            || upload.ModifiedTimeMilliseconds < 0)
        {
            throw new ArgumentException(
                "Zotero 附件上传字段无效或文件超过 12 MiB。",
                nameof(upload));
        }
        byte[] content = upload.Content.ToArray();
        return new(
            upload.ItemKey,
            upload.FileName,
            upload.ContentType,
            content,
            upload.ModifiedTimeMilliseconds,
            ComputeZoteroMd5(content));
    }

    private static bool ValidateMimeType(string? value) =>
        value is not null
        && value.Length <= MaximumMimeTypeLength
        && AllowedAttachmentMimeTypes.Contains(value);

    private static bool ValidateFileName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumFileNameLength
        && !string.Equals(value, ".", StringComparison.Ordinal)
        && !string.Equals(value, "..", StringComparison.Ordinal)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(character =>
            char.IsControl(character)
            || character is '/' or '\\');

    [SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification =
            "Zotero Web API v3 文件上传协议强制使用 MD5 作为内容标识；它不用于认证、签名或安全决策。")]
    private static string ComputeZoteroMd5(byte[] content) =>
        Convert.ToHexString(MD5.HashData(content))
            .ToLowerInvariant();

    private static bool ValidateOptionalText(
        string? value,
        int maximumLength) =>
        value is null
        || (value.Length <= maximumLength
            && !value.Any(char.IsControl)
            && string.Equals(value, value.Trim(), StringComparison.Ordinal));

    private static void ValidateKey(string key, string parameterName)
    {
        if (key.Length != 8
            || key.Any(character => !KeyAlphabet.Contains(
                character,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Zotero 条目 key 必须是官方字符集中的 8 个字符。",
                parameterName);
        }
    }

    private static string ValidateApiKey(string? apiKey)
    {
        if (apiKey is null
            || apiKey.Length is < 1 or > MaximumApiKeyLength
            || apiKey.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException(
                "Zotero API key 格式无效。",
                nameof(apiKey));
        }
        return apiKey;
    }

    private static bool TryGetBoolean(
        JsonElement value,
        string name,
        out bool result)
    {
        result = false;
        if (!value.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        result = property.GetBoolean();
        return true;
    }

    private static bool TryGetOptionalBoolean(
        JsonElement value,
        string name,
        out bool result)
    {
        result = false;
        if (!value.TryGetProperty(name, out JsonElement property))
        {
            return true;
        }
        if (property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        result = property.GetBoolean();
        return true;
    }

    private static bool TryGetString(
        JsonElement value,
        string name,
        out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        result = property.GetString()!;
        return true;
    }

    private static bool TryGetOptionalString(
        JsonElement value,
        string name,
        bool allowFalse,
        out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null
            || (allowFalse && property.ValueKind == JsonValueKind.False))
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        result = property.GetString();
        return result is not null;
    }

    private static ArgumentException InvalidTarget() =>
        new(
            "Zotero 目标必须是 https://api.zotero.org/users/{positive-id}/。",
            "target");

    private static ZoteroApiException Rejected() =>
        new(
            ZoteroApiFailure.Rejected,
            isRetryable: false);

    private static ZoteroApiException Cancelled(Exception _) =>
        new(
            ZoteroApiFailure.Cancelled,
            isRetryable: false);

    internal sealed record ValidatedTarget(
        Uri Endpoint,
        long UserId,
        bool RequireNotesPermission,
        bool RequireFilesPermission);

    private sealed record ValidatedItem(
        ZoteroItem Value,
        string MarkerToken);

    private sealed record ValidatedAttachmentUpload(
        string ItemKey,
        string FileName,
        string ContentType,
        byte[] Content,
        long ModifiedTimeMilliseconds,
        string Md5);

    private sealed record UploadAuthorization(
        bool Exists,
        string? UploadUri,
        MediaTypeHeaderValue? UploadContentType,
        string? Prefix,
        string? Suffix,
        string? UploadKey)
    {
        public static UploadAuthorization Existing { get; } =
            new(true, null, null, null, null, null);
    }

    private sealed record RequestContext(
        ValidatedTarget Target,
        string ApiKey,
        HttpClient Client,
        TargetState State);

    private sealed class TargetState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset PauseUntil { get; set; } =
            DateTimeOffset.MinValue;
    }
}

/// <summary>
/// Zotero 专用健康探针只读取当前目标并验证 key 权限；网络连接复用共享健康服务已固定的地址，绝不写库。
/// </summary>
internal sealed class ZoteroEntryIntegrationHealthProbe(
    IZoteroApiClient client,
    IZoteroExportTargetStore targets)
    : IEntryIntegrationHealthProbe
{
    public EntryIntegrationKind Kind => EntryIntegrationKind.Zotero;

    public async Task<EntryIntegrationProbeResult> ProbeAsync(
        EntryIntegrationProbeContext context,
        string credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ZoteroExportTarget? configured;
        try
        {
            configured = await targets.GetAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(EntryIntegrationHealthStatus.Unavailable);
        }
        if (configured is null)
        {
            return new(EntryIntegrationHealthStatus.Unauthorized);
        }
        if (!Uri.Compare(
                configured.ApiRoot,
                context.Endpoint,
                UriComponents.AbsoluteUri,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase).Equals(0))
        {
            return new(EntryIntegrationHealthStatus.BlockedEndpoint);
        }

        try
        {
            await client.ProbePinnedAsync(
                new(
                    configured.ApiRoot,
                    configured.IncludeSummaryNote,
                    configured.UploadFirstImageAttachment),
                credential,
                context.PinnedAddresses,
                cancellationToken).ConfigureAwait(false);
            return EntryIntegrationProbeResult.Healthy();
        }
        catch (ZoteroApiException exception)
            when (exception.Failure == ZoteroApiFailure.Cancelled
                  && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ZoteroApiException exception)
        {
            return exception.Failure switch
            {
                ZoteroApiFailure.Unauthorized =>
                    new(EntryIntegrationHealthStatus.Unauthorized),
                ZoteroApiFailure.BlockedEndpoint =>
                    new(EntryIntegrationHealthStatus.BlockedEndpoint),
                ZoteroApiFailure.RateLimited =>
                    new(
                        EntryIntegrationHealthStatus.RateLimited,
                        exception.RetryAfter),
                _ => new(EntryIntegrationHealthStatus.Unavailable)
            };
        }
        catch (ArgumentException)
        {
            // 端点已与规范化后的本机目标逐字匹配；此处剩余的参数失败只能来自凭据格式。
            return new(EntryIntegrationHealthStatus.Unauthorized);
        }
        catch
        {
            return new(EntryIntegrationHealthStatus.Unavailable);
        }
    }
}
