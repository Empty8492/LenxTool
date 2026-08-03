using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// exporter 与 Reader Save API 之间的最小文档合同；正文摘录只属于 Summary，
/// Html 与非官方幂等字段刻意不进入此边界。
/// </summary>
public sealed record ReadwiseDocument(
    string Url,
    string? Title,
    string? Author,
    string? Summary,
    string? PublishedDate,
    string? ImageUrl,
    IReadOnlyList<string> Tags,
    string? Notes = null);

/// <summary>
/// Reader 只回传文档标识与站内阅读地址；200 通过 AlreadyExisted 与 201 区分。
/// </summary>
public sealed record ReadwiseSaveResult(
    string Id,
    Uri Url,
    bool AlreadyExisted);

public enum ReadwiseApiFailure
{
    Unauthorized = 1,
    Rejected = 2,
    RateLimited = 3,
    Unavailable = 4,
    UnknownWriteOutcome = 5,
    BlockedEndpoint = 6,
    Cancelled = 7
}

/// <summary>
/// 封闭第三方失败；消息不包含 access token、文章正文、URL 或第三方响应正文。
/// </summary>
public sealed class ReadwiseApiException : Exception
{
    public ReadwiseApiException(
        ReadwiseApiFailure failure,
        bool isRetryable,
        TimeSpan? retryAfter = null)
        : base(CreateMessage(failure))
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }
        Failure = failure;
        IsRetryable = isRetryable;
        RetryAfter = retryAfter;
    }

    public ReadwiseApiFailure Failure { get; }
    public bool IsRetryable { get; }
    public TimeSpan? RetryAfter { get; }

    private static string CreateMessage(ReadwiseApiFailure failure) =>
        failure switch
        {
            ReadwiseApiFailure.Unauthorized =>
                "Readwise 凭据或账户状态不允许当前请求。",
            ReadwiseApiFailure.Rejected =>
                "Readwise 拒绝了请求或返回了不兼容的数据。",
            ReadwiseApiFailure.RateLimited =>
                "Readwise 暂时限制了请求速率。",
            ReadwiseApiFailure.Unavailable =>
                "Readwise 服务暂时不可用。",
            ReadwiseApiFailure.UnknownWriteOutcome =>
                "Readwise 写入结果暂时无法确认。",
            ReadwiseApiFailure.BlockedEndpoint =>
                "Readwise 网络目标未通过安全校验。",
            ReadwiseApiFailure.Cancelled =>
                "Readwise 请求已取消。",
            _ => "Readwise 请求失败。"
        };
}

public interface IReadwiseApiClient
{
    Task ProbeAsync(
        string accessToken,
        CancellationToken cancellationToken);

    Task ProbePinnedAsync(
        string accessToken,
        IReadOnlyList<IPAddress> pinnedAddresses,
        CancellationToken cancellationToken);

    Task<ReadwiseSaveResult> SaveAsync(
        string accessToken,
        ReadwiseDocument document,
        CancellationToken cancellationToken);
}

internal interface IReadwiseHttpClientFactory
{
    HttpClient Create(
        Uri endpoint,
        IReadOnlyList<IPAddress> pinnedAddresses);
}

internal interface IReadwiseClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reader 连接固定到预解析公网地址，并关闭所有可能改变目标或隐式处理正文的功能。
/// </summary>
internal static class ReadwiseHttpClientSecurity
{
    public static SocketsHttpHandler CreatePrimaryHandler(
        IReadOnlyList<IPAddress> pinnedAddresses)
    {
        SocketsHttpHandler handler = PinnedHttpHandlerFactory.Create(
            ReadwiseApiClient.ApiRoot,
            pinnedAddresses,
            TimeSpan.FromSeconds(5),
            DecompressionMethods.None);
        handler.MaxConnectionsPerServer = 1;
        return handler;
    }
}

internal sealed class PinnedReadwiseHttpClientFactory
    : IReadwiseHttpClientFactory
{
    public HttpClient Create(
        Uri endpoint,
        IReadOnlyList<IPAddress> pinnedAddresses)
    {
        if (endpoint != ReadwiseApiClient.ApiRoot)
        {
            throw new ArgumentException(
                "Readwise 客户端只能连接官方固定根地址。",
                nameof(endpoint));
        }
        return new(
            ReadwiseHttpClientSecurity.CreatePrimaryHandler(pinnedAddresses),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}

internal sealed class SystemReadwiseClock : IReadwiseClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Readwise Reader API 安全客户端。
/// 来源：https://readwise.io/reader_api
/// </summary>
internal sealed class ReadwiseApiClient : IReadwiseApiClient, IDisposable
{
    internal static Uri ApiRoot { get; } = new("https://readwise.io/");

    private const int MaximumRequestBytes = 128 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private const int MaximumTokenLength = 512;
    private const int MaximumUrlLength = 2048;
    private const int MaximumTitleLength = 1024;
    private const int MaximumAuthorLength = 1024;
    private const int MaximumSummaryLength = 64 * 1024;
    private const int MaximumPublishedDateLength = 128;
    private const int MaximumTagCount = 64;
    private const int MaximumTagLength = 256;
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PermitInterval =
        TimeSpan.FromSeconds(60d / 50d);
    private static readonly TimeSpan MaximumServerPause =
        TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 8
        };
    private static readonly Uri AuthEndpoint =
        new(ApiRoot, "api/v2/auth/");
    private static readonly Uri SaveEndpoint =
        new(ApiRoot, "api/v3/save/");

    private readonly IFeedHostResolver _resolver;
    private readonly IReadwiseHttpClientFactory _clients;
    private readonly IReadwiseClock _clock;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastRequestStartedAt;
    private DateTimeOffset _pauseUntil = DateTimeOffset.MinValue;
    private bool _disposed;

    public ReadwiseApiClient(IFeedHostResolver resolver)
        : this(
            resolver,
            new PinnedReadwiseHttpClientFactory(),
            new SystemReadwiseClock())
    {
    }

    internal ReadwiseApiClient(
        IFeedHostResolver resolver,
        IReadwiseHttpClientFactory clients,
        IReadwiseClock clock,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(clock);
        TimeSpan effectiveTimeout = requestTimeout ?? RequestTimeout;
        if (effectiveTimeout <= TimeSpan.Zero
            || effectiveTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
        _resolver = resolver;
        _clients = clients;
        _clock = clock;
        _requestTimeout = effectiveTimeout;
    }

    public async Task ProbeAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCancelled(cancellationToken);
        string credential = ValidateAccessToken(accessToken);
        await EnterGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfServerPaused();
            IReadOnlyList<IPAddress> addresses =
                await ResolvePublicAsync(cancellationToken)
                    .ConfigureAwait(false);
            await WaitForPermitAsync(cancellationToken)
                .ConfigureAwait(false);
            using HttpClient client = CreatePinnedClient(addresses);
            await ProbeCoreAsync(
                    client,
                    credential,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ProbePinnedAsync(
        string accessToken,
        IReadOnlyList<IPAddress> pinnedAddresses,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCancelled(cancellationToken);
        string credential = ValidateAccessToken(accessToken);
        IReadOnlyList<IPAddress> addresses =
            ValidatePublicAddresses(pinnedAddresses);
        await EnterGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfServerPaused();
            await WaitForPermitAsync(cancellationToken)
                .ConfigureAwait(false);
            using HttpClient client = CreatePinnedClient(addresses);
            await ProbeCoreAsync(
                    client,
                    credential,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ReadwiseSaveResult> SaveAsync(
        string accessToken,
        ReadwiseDocument document,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCancelled(cancellationToken);
        string credential = ValidateAccessToken(accessToken);
        SaveRequest payload = ValidateDocument(document);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            JsonOptions);
        if (json.Length > MaximumRequestBytes)
        {
            throw Rejected();
        }

        await EnterGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfServerPaused();
            IReadOnlyList<IPAddress> addresses =
                await ResolvePublicAsync(cancellationToken)
                    .ConfigureAwait(false);
            await WaitForPermitAsync(cancellationToken)
                .ConfigureAwait(false);
            using HttpClient client = CreatePinnedClient(addresses);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                SaveEndpoint);
            AddHeaders(request, credential);
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType =
                new("application/json")
                {
                    CharSet = "utf-8"
                };
            // 同一个超时令牌覆盖响应头与有界 JSON 正文读取；否则恶意或故障端点
            // 可以在返回响应头后无限阻塞耐久队列。
            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            using HttpResponseMessage response = await SendAsync(
                    client,
                    request,
                    isWrite: true,
                    timeout.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            TimeSpan? retryAfter = ObserveRetryAfter(response);
            if (response.StatusCode is HttpStatusCode.OK
                or HttpStatusCode.Created)
            {
                return await ParseSaveResultAsync(
                        response,
                        response.StatusCode == HttpStatusCode.OK,
                        timeout.Token,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            if (response.IsSuccessStatusCode)
            {
                // 官方只承诺 200/201；其他 2xx 可能已写入，不能误报为可安全丢弃的拒绝。
                throw UnknownWriteOutcome();
            }
            throw MapStatus(response.StatusCode, retryAfter);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProbeCoreAsync(
        HttpClient client,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            AuthEndpoint);
        AddHeaders(request, accessToken);
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using HttpResponseMessage response = await SendAsync(
                client,
                request,
                isWrite: false,
                timeout.Token,
                cancellationToken)
            .ConfigureAwait(false);
        TimeSpan? retryAfter = ObserveRetryAfter(response);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return;
        }
        throw MapStatus(response.StatusCode, retryAfter);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gate.Dispose();
        _disposed = true;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        bool isWrite,
        CancellationToken operationCancellationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            return await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    operationCancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (callerCancellationToken.IsCancellationRequested)
            {
                throw Cancelled();
            }
            throw isWrite ? UnknownWriteOutcome() : Unavailable();
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                  or IOException
                  or SocketException
                  or InvalidOperationException)
        {
            throw isWrite ? UnknownWriteOutcome() : Unavailable();
        }
    }

    private static async Task<ReadwiseSaveResult> ParseSaveResultAsync(
        HttpResponseMessage response,
        bool alreadyExisted,
        CancellationToken operationCancellationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(
                    mediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw UnknownWriteOutcome();
            }
            byte[] body = await ReadBoundedAsync(
                    response.Content,
                    operationCancellationToken)
                .ConfigureAwait(false);
            using JsonDocument json = JsonDocument.Parse(
                body,
                new JsonDocumentOptions
                {
                    MaxDepth = 8,
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false
                });
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetSafeString(
                    root,
                    "id",
                    maximumLength: 256,
                    out string id)
                || !TryGetSafeString(
                    root,
                    "url",
                    MaximumUrlLength,
                    out string rawUrl)
                || !TryValidateReaderUrl(rawUrl, out Uri readerUrl))
            {
                throw UnknownWriteOutcome();
            }
            return new(id, readerUrl, alreadyExisted);
        }
        catch (ReadwiseApiException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (callerCancellationToken.IsCancellationRequested)
            {
                throw Cancelled();
            }
            throw UnknownWriteOutcome();
        }
        catch (Exception exception)
            when (exception is JsonException
                  or IOException
                  or InvalidOperationException)
        {
            throw UnknownWriteOutcome();
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw UnknownWriteOutcome();
        }
        await using Stream stream = await content.ReadAsStreamAsync(
                cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(
                    chunk.AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw UnknownWriteOutcome();
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task<IReadOnlyList<IPAddress>> ResolvePublicAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<IPAddress> addresses =
                await _resolver.ResolveAsync(
                        ApiRoot.IdnHost,
                        cancellationToken)
                    .ConfigureAwait(false);
            return ValidatePublicAddresses(addresses);
        }
        catch (ReadwiseApiException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw Cancelled();
            }
            throw Unavailable();
        }
        catch (Exception exception)
            when (exception is SocketException
                  or ArgumentException
                  or InvalidOperationException)
        {
            throw Unavailable();
        }
    }

    private static ReadOnlyCollection<IPAddress> ValidatePublicAddresses(
        IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Any(address => address is null))
        {
            throw BlockedEndpoint();
        }
        IPAddress[] distinct = addresses.Distinct().ToArray();
        if (distinct.Length == 0
            || distinct.Any(address =>
                NetworkTargetClassifier.Classify(address)
                    is NetworkAddressDisposition.Private
                    or NetworkAddressDisposition.Forbidden))
        {
            throw BlockedEndpoint();
        }
        return Array.AsReadOnly(distinct);
    }

    private HttpClient CreatePinnedClient(
        IReadOnlyList<IPAddress> addresses)
    {
        try
        {
            return _clients.Create(ApiRoot, addresses);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                  or InvalidOperationException
                  or HttpRequestException
                  or IOException
                  or SocketException)
        {
            throw Unavailable();
        }
    }

    private async Task EnterGateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw Cancelled();
        }
    }

    private async Task WaitForPermitAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfServerPaused();
        DateTimeOffset permittedAt = _lastRequestStartedAt is DateTimeOffset previous
            ? previous + PermitInterval
            : DateTimeOffset.MinValue;
        TimeSpan delay = permittedAt - _clock.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            // 只有主动 50/min 平滑节流会在适配器内短等；服务端暂停必须交还
            // 耐久队列调度，避免一个 Readwise 任务占住全局导出 worker。
            try
            {
                await _clock.DelayAsync(delay, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw Cancelled();
            }
        }
        _lastRequestStartedAt = _clock.UtcNow;
    }

    private void ThrowIfServerPaused()
    {
        TimeSpan remaining = _pauseUntil - _clock.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            throw new ReadwiseApiException(
                ReadwiseApiFailure.RateLimited,
                isRetryable: true,
                remaining);
        }
    }

    private TimeSpan? ObserveRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? value = response.Headers.RetryAfter;
        TimeSpan? retryAfter = value?.Delta;
        if (retryAfter is null && value?.Date is DateTimeOffset date)
        {
            retryAfter = date - _clock.UtcNow;
        }
        if (retryAfter is null) return null;
        if (retryAfter < TimeSpan.Zero)
        {
            retryAfter = TimeSpan.Zero;
        }
        if (retryAfter > MaximumServerPause)
        {
            retryAfter = MaximumServerPause;
        }
        DateTimeOffset pauseUntil = _clock.UtcNow + retryAfter.Value;
        if (pauseUntil > _pauseUntil)
        {
            _pauseUntil = pauseUntil;
        }
        return retryAfter;
    }

    private static SaveRequest ValidateDocument(ReadwiseDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string url = ValidateExternalUrl(document.Url, nameof(document));
        string? title = ValidateOptionalText(
            document.Title,
            MaximumTitleLength,
            allowLineBreaks: false,
            nameof(document));
        string? author = ValidateOptionalText(
            document.Author,
            MaximumAuthorLength,
            allowLineBreaks: false,
            nameof(document));
        string? summary = ValidateOptionalText(
            document.Summary,
            MaximumSummaryLength,
            allowLineBreaks: true,
            nameof(document));
        string? publishedDate = ValidatePublishedDate(
            document.PublishedDate,
            nameof(document));
        string? imageUrl = document.ImageUrl is null
            ? null
            : ValidateExternalUrl(document.ImageUrl, nameof(document));
        string? notes = ValidateOptionalText(
            document.Notes,
            MaximumSummaryLength,
            allowLineBreaks: true,
            nameof(document));
        ArgumentNullException.ThrowIfNull(document.Tags);
        if (document.Tags.Count > MaximumTagCount)
        {
            throw new ArgumentException(
                "Readwise 文档标签数量无效。",
                nameof(document));
        }
        string[] tags = document.Tags
            .Select(tag => ValidateRequiredText(
                tag,
                MaximumTagLength,
                allowLineBreaks: false,
                nameof(document)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new(
            url,
            title,
            author,
            summary,
            publishedDate,
            imageUrl,
            Location: "new",
            Category: "article",
            SavedUsing: "lenxtool",
            tags,
            notes);
    }

    private static string ValidateExternalUrl(
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumUrlLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (!string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.Ordinal)
                && !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.Ordinal))
            || !uri.IsDefaultPort
            || string.IsNullOrEmpty(uri.IdnHost)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || NetworkTargetClassifier.IsReservedHostName(uri.IdnHost))
        {
            throw new ArgumentException(
                "Readwise 文档 URL 无效。",
                parameterName);
        }
        if (IPAddress.TryParse(uri.IdnHost, out IPAddress? address)
            && NetworkTargetClassifier.Classify(address)
                != NetworkAddressDisposition.Public)
        {
            throw new ArgumentException(
                "Readwise 文档 URL 无效。",
                parameterName);
        }
        return value;
    }

    private static string? ValidatePublishedDate(
        string? value,
        string parameterName)
    {
        if (value is null) return null;
        if (value.Length is 0 or > MaximumPublishedDateLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            throw new ArgumentException(
                "Readwise 发布时间必须是 ISO 8601 时间。",
                parameterName);
        }
        return value;
    }

    private static string? ValidateOptionalText(
        string? value,
        int maximumLength,
        bool allowLineBreaks,
        string parameterName) =>
        value is null
            ? null
            : ValidateRequiredText(
                value,
                maximumLength,
                allowLineBreaks,
                parameterName);

    private static string ValidateRequiredText(
        string? value,
        int maximumLength,
        bool allowLineBreaks,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(character =>
                char.IsControl(character)
                && !(allowLineBreaks
                    && character is '\r' or '\n' or '\t')))
        {
            throw new ArgumentException(
                "Readwise 文档字段无效。",
                parameterName);
        }
        return value;
    }

    private static string ValidateAccessToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumTokenLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(character =>
                character is < (char)0x21 or > (char)0x7E))
        {
            throw new ArgumentException("Readwise access token 无效。", nameof(value));
        }
        return value;
    }

    private static bool TryGetSafeString(
        JsonElement root,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string? candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > maximumLength
            || !string.Equals(
                candidate,
                candidate.Trim(),
                StringComparison.Ordinal)
            || candidate.Any(char.IsControl))
        {
            return false;
        }
        value = candidate;
        return true;
    }

    private static bool TryValidateReaderUrl(
        string value,
        out Uri result)
    {
        result = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || !string.Equals(
                uri.IdnHost,
                "read.readwise.io",
                StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }
        result = uri;
        return true;
    }

    private static void AddHeaders(
        HttpRequestMessage request,
        string accessToken)
    {
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Token", accessToken);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static ReadwiseApiException MapStatus(
        HttpStatusCode status,
        TimeSpan? retryAfter) =>
        status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new(ReadwiseApiFailure.Unauthorized, isRetryable: false),
            HttpStatusCode.BadRequest
                or HttpStatusCode.UnprocessableEntity =>
                Rejected(),
            HttpStatusCode.TooManyRequests =>
                new(
                    ReadwiseApiFailure.RateLimited,
                    isRetryable: true,
                    retryAfter),
            HttpStatusCode.RequestTimeout =>
                Unavailable(retryAfter),
            >= HttpStatusCode.InternalServerError =>
                Unavailable(retryAfter),
            _ => Rejected()
        };

    private static void ThrowIfCancelled(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw Cancelled();
        }
    }

    private static ReadwiseApiException Rejected() =>
        new(ReadwiseApiFailure.Rejected, isRetryable: false);

    private static ReadwiseApiException Unavailable(
        TimeSpan? retryAfter = null) =>
        new(
            ReadwiseApiFailure.Unavailable,
            isRetryable: true,
            retryAfter);

    private static ReadwiseApiException UnknownWriteOutcome() =>
        new(ReadwiseApiFailure.UnknownWriteOutcome, isRetryable: true);

    private static ReadwiseApiException BlockedEndpoint() =>
        new(ReadwiseApiFailure.BlockedEndpoint, isRetryable: false);

    private static ReadwiseApiException Cancelled() =>
        new(ReadwiseApiFailure.Cancelled, isRetryable: false);

    private sealed record SaveRequest(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("published_date")] string? PublishedDate,
        [property: JsonPropertyName("image_url")] string? ImageUrl,
        [property: JsonPropertyName("location")] string Location,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("saved_using")] string SavedUsing,
        [property: JsonPropertyName("tags")]
        IReadOnlyList<string> Tags,
        [property: JsonPropertyName("notes")] string? Notes);
}
