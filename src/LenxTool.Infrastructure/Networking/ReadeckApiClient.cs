using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// Readeck 客户端先按稳定技术标签查找，再创建或更新；未知创建结果可由下次重放收敛。
/// </summary>
internal sealed class ReadeckApiClient : IReadeckApiClient
{
    private const int MaximumRequestBytes = 16 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { MaxDepth = 12 };
    private readonly IIntegrationHttpClientFactory _clients;
    private readonly TimeSpan _requestTimeout;

    public ReadeckApiClient()
        : this(new PinnedIntegrationHttpClientFactory(), DefaultRequestTimeout)
    {
    }

    internal ReadeckApiClient(
        IIntegrationHttpClientFactory clients,
        TimeSpan? requestTimeout = null)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero
            || _requestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
    }

    public async Task ProbeAsync(
        EntryIntegrationProbeContext context,
        string token,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        using HttpClient client = _clients.Create(context);
        using HttpResponseMessage response = await SendAsync(
                client,
                CreateRequest(
                    HttpMethod.Get,
                    new Uri(context.Endpoint, "api/bookmarks?limit=1"),
                    ValidateToken(token)),
                isWrite: false,
                _requestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        _ = await ReadJsonAsync(
                response,
                isWrite: false,
                _requestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ReadeckBookmarkResult> UpsertAsync(
        EntryIntegrationProbeContext context,
        string token,
        ReadeckBookmark bookmark,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        ValidateBookmark(bookmark);
        string credential = ValidateToken(token);
        using HttpClient client = _clients.Create(context);
        ReadeckBookmarkResult? existing = await FindAsync(
                client,
                context,
                credential,
                bookmark.StableLabel,
                _requestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            existing = await CreateAsync(
                    client,
                    context,
                    credential,
                    bookmark,
                    _requestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        await UpdateAsync(
                client,
                context,
                credential,
                existing.Id,
                bookmark,
                _requestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return existing;
    }

    private static async Task<ReadeckBookmarkResult?> FindAsync(
        HttpClient client,
        EntryIntegrationProbeContext context,
        string token,
        string label,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        string path = $"api/bookmarks?labels={Uri.EscapeDataString(label)}&limit=2";
        using HttpResponseMessage response = await SendAsync(
                client,
                CreateRequest(
                    HttpMethod.Get,
                    new Uri(context.Endpoint, path),
                    token),
                isWrite: false,
                requestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument json = await ReadJsonAsync(
                response,
                isWrite: false,
                requestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw Failure(ReadeckApiFailure.Rejected);
        }
        JsonElement[] items = json.RootElement
            .EnumerateArray()
            .ToArray();
        JsonElement[] matches = items
            .Where(item => HasLabel(item, label))
            .ToArray();
        if (!response.Headers.TryGetValues(
                "Total-Count",
                out IEnumerable<string>? totals)
            || !TryGetSingleValue(totals, out string total)
            || !int.TryParse(
                total,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int count)
            || count < 0)
        {
            throw Failure(ReadeckApiFailure.Unavailable);
        }
        if (items.Length == 0 && count == 0) return null;
        if (matches.Length > 1 || count > 1)
        {
            throw Failure(ReadeckApiFailure.Conflict);
        }
        if (items.Length != 1 || matches.Length != 1 || count != 1)
        {
            // Never create when the server's page, count, or label projection
            // disagree; this can be a transient index lag after a successful
            // POST and must remain safely retryable.
            throw Failure(ReadeckApiFailure.Unavailable);
        }
        string id = GetRequiredString(matches[0], "id", 256);
        string href = GetRequiredString(matches[0], "href", 2048);
        return new(id, ValidateResultUrl(context.Endpoint, href));
    }

    private static async Task<ReadeckBookmarkResult> CreateAsync(
        HttpClient client,
        EntryIntegrationProbeContext context,
        string token,
        ReadeckBookmark bookmark,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["url"] = bookmark.SourceUrl.AbsoluteUri,
            ["title"] = bookmark.Title,
            ["labels"] = bookmark.Labels
        };
        using HttpResponseMessage response = await SendAsync(
                client,
                CreateJsonRequest(
                    HttpMethod.Post,
                    new Uri(context.Endpoint, "api/bookmarks"),
                    token,
                    payload),
                isWrite: true,
                requestTimeout,
                cancellationToken,
                HttpStatusCode.Accepted)
            .ConfigureAwait(false);
        string id = GetRequiredHeader(response, "Bookmark-Id", 256);
        Uri url;
        try
        {
            url = response.Headers.Location is null
                ? new Uri(
                    context.Endpoint,
                    $"api/bookmarks/{Uri.EscapeDataString(id)}")
                : ValidateResultUrl(
                    context.Endpoint,
                    response.Headers.Location.OriginalString);
        }
        catch (Exception exception)
            when (exception is ReadeckApiException
                or UriFormatException)
        {
            throw Failure(ReadeckApiFailure.UnknownWriteOutcome);
        }
        return new(id, url);
    }

    private static async Task UpdateAsync(
        HttpClient client,
        EntryIntegrationProbeContext context,
        string token,
        string id,
        ReadeckBookmark bookmark,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = bookmark.Title,
            ["labels"] = bookmark.Labels,
            ["is_archived"] = bookmark.IsArchived
        };
        using HttpResponseMessage response = await SendAsync(
                client,
                CreateJsonRequest(
                    HttpMethod.Patch,
                    new Uri(
                        context.Endpoint,
                        $"api/bookmarks/{Uri.EscapeDataString(id)}"),
                    token,
                    payload),
                isWrite: true,
                requestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument json = await ReadJsonAsync(
                response,
                isWrite: true,
                requestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !string.Equals(
                    GetRequiredString(json.RootElement, "id", 256),
                    id,
                    StringComparison.Ordinal))
            {
                throw Failure(ReadeckApiFailure.UnknownWriteOutcome);
            }
        }
        catch (ReadeckApiException)
        {
            throw Failure(ReadeckApiFailure.UnknownWriteOutcome);
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        Uri uri,
        string token,
        object payload)
    {
        HttpRequestMessage request = CreateRequest(method, uri, token);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (bytes.Length > MaximumRequestBytes)
        {
            request.Dispose();
            throw Failure(ReadeckApiFailure.Rejected);
        }
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        bool isWrite,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken,
        HttpStatusCode? exactStatus = null)
    {
        using (request)
        using (var timeout = CancellationTokenSource
                   .CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(requestTimeout);
            try
            {
                HttpResponseMessage response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);
                bool success = exactStatus is null
                    ? response.IsSuccessStatusCode
                    : response.StatusCode == exactStatus;
                if (success) return response;
                ReadeckApiException failure = MapStatus(response);
                response.Dispose();
                throw failure;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw Failure(ReadeckApiFailure.Cancelled);
            }
            catch (ReadeckApiException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is HttpRequestException
                    or OperationCanceledException)
            {
                throw Failure(
                    isWrite
                        ? ReadeckApiFailure.UnknownWriteOutcome
                        : ReadeckApiFailure.Unavailable);
            }
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        bool isWrite,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await BoundedHttpContent
                .ReadAsByteArrayAsync(
                    response.Content,
                    MaximumResponseBytes,
                    requestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                throw Failure(
                    isWrite
                        ? ReadeckApiFailure.UnknownWriteOutcome
                        : ReadeckApiFailure.Rejected);
            }
            return JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions { MaxDepth = 12 });
        }
        catch (ReadeckApiException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw Failure(ReadeckApiFailure.Cancelled);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException
                or HttpRequestException
                or IOException)
        {
            throw Failure(
                isWrite
                    ? ReadeckApiFailure.UnknownWriteOutcome
                    : ReadeckApiFailure.Unavailable);
        }
        catch
        {
            throw Failure(
                isWrite
                    ? ReadeckApiFailure.UnknownWriteOutcome
                    : ReadeckApiFailure.Rejected);
        }
    }

    private static ReadeckApiException MapStatus(HttpResponseMessage response) =>
        response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                Failure(ReadeckApiFailure.Unauthorized),
            HttpStatusCode.TooManyRequests =>
                Failure(
                    ReadeckApiFailure.RateLimited,
                    response.Headers.RetryAfter?.Delta),
            >= HttpStatusCode.InternalServerError =>
                Failure(ReadeckApiFailure.Unavailable),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest =>
                Failure(ReadeckApiFailure.BlockedEndpoint),
            _ => Failure(ReadeckApiFailure.Rejected)
        };

    private static void ValidateContext(EntryIntegrationProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Endpoint.AbsolutePath != "/"
            || context.PinnedAddresses.Count == 0)
        {
            throw Failure(ReadeckApiFailure.BlockedEndpoint);
        }
    }

    private static string ValidateToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > 2048
            || token.Any(char.IsControl)
            || !string.Equals(token, token.Trim(), StringComparison.Ordinal))
        {
            throw Failure(ReadeckApiFailure.Unauthorized);
        }
        return token;
    }

    private static void ValidateBookmark(ReadeckBookmark value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.StableLabel.StartsWith("lenxtool:", StringComparison.Ordinal)
            || value.StableLabel.Length > 128
            || value.SourceUrl.AbsoluteUri.Length > 1024
            || value.SourceUrl.Scheme != Uri.UriSchemeHttp
                && value.SourceUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(value.SourceUrl.UserInfo)
            || string.IsNullOrWhiteSpace(value.Title)
            || value.Title.Length > 1024
            || value.Labels.Count is < 1 or > 64
            || !value.Labels.Contains(value.StableLabel, StringComparer.Ordinal)
            || value.Labels.Any(label =>
                string.IsNullOrWhiteSpace(label)
                || label.Length > 128
                || label.Any(char.IsControl)))
        {
            throw Failure(ReadeckApiFailure.Rejected);
        }
    }

    private static bool HasLabel(JsonElement item, string label) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("labels", out JsonElement labels)
        && labels.ValueKind == JsonValueKind.Array
        && labels.EnumerateArray().Any(value =>
            value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), label, StringComparison.Ordinal));

    private static string GetRequiredString(
        JsonElement item,
        string name,
        int maximumLength)
    {
        if (!item.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Length > maximumLength)
        {
            throw Failure(ReadeckApiFailure.Rejected);
        }
        return value.GetString()!;
    }

    private static string GetRequiredHeader(
        HttpResponseMessage response,
        string name,
        int maximumLength)
    {
        if (!response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            || !TryGetSingleValue(values, out string value)
            || string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw Failure(ReadeckApiFailure.UnknownWriteOutcome);
        }
        return value;
    }

    private static bool TryGetSingleValue(
        IEnumerable<string> values,
        out string value)
    {
        using IEnumerator<string> enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            value = string.Empty;
            return false;
        }
        value = enumerator.Current;
        return !enumerator.MoveNext();
    }

    private static Uri ValidateResultUrl(Uri endpoint, string value)
    {
        Uri result = new(endpoint, value);
        if (!result.IsAbsoluteUri
            || !string.Equals(result.Scheme, endpoint.Scheme, StringComparison.Ordinal)
            || !string.Equals(result.IdnHost, endpoint.IdnHost, StringComparison.Ordinal)
            || result.Port != endpoint.Port
            || !string.IsNullOrEmpty(result.UserInfo))
        {
            throw Failure(ReadeckApiFailure.Rejected);
        }
        return result;
    }

    private static ReadeckApiException Failure(
        ReadeckApiFailure failure,
        TimeSpan? retryAfter = null) => new(failure, retryAfter);
}
