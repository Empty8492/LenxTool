using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal sealed class QBittorrentApiClient : IQBittorrentApiClient
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly Version MinimumWebApiVersion = new(2, 14, 1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly IIntegrationHttpClientFactory _clients;

    public QBittorrentApiClient()
        : this(new PinnedIntegrationHttpClientFactory())
    {
    }

    internal QBittorrentApiClient(IIntegrationHttpClientFactory clients)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
    }

    public async Task ProbeAsync(
        EntryIntegrationProbeContext context,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        string credential = ValidateApiKey(apiKey);
        using HttpClient client = _clients.Create(context);
        await EnsureSupportedVersionAsync(
                client,
                context,
                credential,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureSupportedVersionAsync(
        HttpClient client,
        EntryIntegrationProbeContext context,
        string credential,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
                client,
                CreateRequest(
                    HttpMethod.Get,
                    new Uri(context.Endpoint, "api/v2/app/webapiVersion"),
                    context.Endpoint,
                    credential),
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        string raw = await ReadTextAsync(
                response,
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Version.TryParse(raw.Trim(), out Version? version)
            || version < MinimumWebApiVersion)
        {
            throw Failure(QBittorrentApiFailure.UnsupportedVersion);
        }
    }

    public async Task AddAsync(
        EntryIntegrationProbeContext context,
        string apiKey,
        QBittorrentSource source,
        string category,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        string credential = ValidateApiKey(apiKey);
        ValidateSource(source);
        ValidateCategory(category);
        using HttpClient client = _clients.Create(context);
        await EnsureSupportedVersionAsync(
                client,
                context,
                credential,
                cancellationToken)
            .ConfigureAwait(false);
        await EnsureCategoryExistsAsync(
                client,
                context,
                credential,
                category,
                cancellationToken)
            .ConfigureAwait(false);
        TorrentLookupState existing = await GetTorrentStateAsync(
                client,
                context,
                credential,
                source.InfoHash,
                category,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing == TorrentLookupState.Matching)
        {
            return;
        }
        if (existing == TorrentLookupState.WrongCategory)
        {
            throw Failure(QBittorrentApiFailure.Conflict);
        }
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            new Uri(context.Endpoint, "api/v2/torrents/add"),
            context.Endpoint,
            credential);
        var form = new MultipartFormDataContent();
        request.Content = form;
        if (source is QBittorrentMagnetSource magnet)
        {
            form.Add(new StringContent(magnet.Magnet, Encoding.UTF8), "urls");
        }
        else if (source is QBittorrentFileSource file)
        {
            var content = new ByteArrayContent(file.Content);
            content.Headers.ContentType =
                new MediaTypeHeaderValue("application/x-bittorrent");
            form.Add(content, "torrents", $"{file.InfoHash}.torrent");
        }
        form.Add(new StringContent(category, Encoding.UTF8), "category");
        using HttpResponseMessage response = await SendAsync(
                client,
                request,
                isWrite: true,
                cancellationToken,
                allowConflict: true)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            string body = await ReadTextAsync(
                    response,
                    isWrite: true,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateAddResponse(response.StatusCode, body, source.InfoHash);
        }

        TorrentLookupState verified;
        try
        {
            verified = await GetTorrentStateAsync(
                    client,
                    context,
                    credential,
                    source.InfoHash,
                    category,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (QBittorrentApiException exception)
            when (exception.Failure is not (
                QBittorrentApiFailure.Unauthorized
                or QBittorrentApiFailure.BlockedEndpoint
                or QBittorrentApiFailure.Cancelled))
        {
            throw Failure(QBittorrentApiFailure.UnknownWriteOutcome);
        }
        if (verified == TorrentLookupState.Matching)
        {
            return;
        }
        if (verified == TorrentLookupState.WrongCategory)
        {
            throw Failure(QBittorrentApiFailure.Conflict);
        }
        throw Failure(
            response.StatusCode == HttpStatusCode.Conflict
                ? QBittorrentApiFailure.Rejected
                : QBittorrentApiFailure.UnknownWriteOutcome);
    }

    private static async Task<TorrentLookupState> GetTorrentStateAsync(
        HttpClient client,
        EntryIntegrationProbeContext context,
        string apiKey,
        string infoHash,
        string category,
        CancellationToken cancellationToken)
    {
        string relative = "api/v2/torrents/info?hashes="
            + Uri.EscapeDataString(infoHash);
        using HttpResponseMessage response = await SendAsync(
                client,
                CreateRequest(
                    HttpMethod.Get,
                    new Uri(context.Endpoint, relative),
                    context.Endpoint,
                    apiKey),
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        string body = await ReadTextAsync(
                response,
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using JsonDocument json = JsonDocument.Parse(
                body,
                new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw Failure(QBittorrentApiFailure.Rejected);
            }
            JsonElement.ArrayEnumerator items =
                json.RootElement.EnumerateArray();
            if (!items.MoveNext())
            {
                return TorrentLookupState.Missing;
            }
            JsonElement item = items.Current;
            if (items.MoveNext()
                || item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("hash", out JsonElement hash)
                || hash.ValueKind != JsonValueKind.String
                || !string.Equals(
                    hash.GetString(),
                    infoHash,
                    StringComparison.OrdinalIgnoreCase)
                || !item.TryGetProperty("category", out JsonElement foundCategory)
                || foundCategory.ValueKind != JsonValueKind.String)
            {
                throw Failure(QBittorrentApiFailure.Rejected);
            }
            return string.Equals(
                    foundCategory.GetString(),
                    category,
                    StringComparison.Ordinal)
                ? TorrentLookupState.Matching
                : TorrentLookupState.WrongCategory;
        }
        catch (QBittorrentApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Failure(QBittorrentApiFailure.Rejected);
        }
    }

    private static void ValidateAddResponse(
        HttpStatusCode statusCode,
        string body,
        string infoHash)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(
                body,
                new JsonDocumentOptions { MaxDepth = 6 });
            JsonElement root = json.RootElement;
            int success = GetRequiredCount(root, "success_count");
            int pending = GetRequiredCount(root, "pending_count");
            int failure = GetRequiredCount(root, "failure_count");
            if (!root.TryGetProperty(
                    "added_torrent_ids",
                    out JsonElement ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                throw Failure(QBittorrentApiFailure.UnknownWriteOutcome);
            }

            var addedIds = new List<string>();
            foreach (JsonElement value in ids.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String
                    || value.GetString() is not string id)
                {
                    throw Failure(
                        QBittorrentApiFailure.UnknownWriteOutcome);
                }
                addedIds.Add(id);
            }
            bool exactSuccess = statusCode == HttpStatusCode.OK
                && success == 1
                && pending == 0
                && failure == 0
                && addedIds.Count == 1
                && string.Equals(
                    addedIds[0],
                    infoHash,
                    StringComparison.OrdinalIgnoreCase);
            bool exactPending = statusCode == HttpStatusCode.Accepted
                && success == 0
                && pending == 1
                && failure == 0
                && addedIds.Count == 0;
            if (!exactSuccess && !exactPending)
            {
                throw Failure(QBittorrentApiFailure.UnknownWriteOutcome);
            }
        }
        catch (QBittorrentApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Failure(QBittorrentApiFailure.UnknownWriteOutcome);
        }
    }

    private static int GetRequiredCount(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int result)
            || result < 0)
        {
            throw Failure(QBittorrentApiFailure.UnknownWriteOutcome);
        }
        return result;
    }

    private static async Task EnsureCategoryExistsAsync(
        HttpClient client,
        EntryIntegrationProbeContext context,
        string apiKey,
        string category,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
                client,
                CreateRequest(
                    HttpMethod.Get,
                    new Uri(context.Endpoint, "api/v2/torrents/categories"),
                    context.Endpoint,
                    apiKey),
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        string body = await ReadTextAsync(
                response,
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using JsonDocument json = JsonDocument.Parse(
                body,
                new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty(category, out _))
            {
                throw Failure(QBittorrentApiFailure.Rejected);
            }
        }
        catch (QBittorrentApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Failure(QBittorrentApiFailure.Rejected);
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        Uri root,
        string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Referrer = root;
        request.Headers.TryAddWithoutValidation(
            "Origin",
            root.GetLeftPart(UriPartial.Authority));
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        bool isWrite,
        CancellationToken cancellationToken,
        bool allowConflict = false)
    {
        using (request)
        using (var timeout = CancellationTokenSource
                   .CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RequestTimeout);
            try
            {
                HttpResponseMessage response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode
                    || allowConflict
                        && response.StatusCode == HttpStatusCode.Conflict)
                {
                    return response;
                }
                QBittorrentApiException failure = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        Failure(QBittorrentApiFailure.Unauthorized),
                    HttpStatusCode.TooManyRequests =>
                        Failure(
                            QBittorrentApiFailure.RateLimited,
                            response.Headers.RetryAfter?.Delta),
                    HttpStatusCode.UnsupportedMediaType =>
                        Failure(QBittorrentApiFailure.Rejected),
                    >= HttpStatusCode.InternalServerError when isWrite =>
                        Failure(QBittorrentApiFailure.UnknownWriteOutcome),
                    >= HttpStatusCode.InternalServerError =>
                        Failure(QBittorrentApiFailure.Unavailable),
                    >= HttpStatusCode.MultipleChoices
                        and < HttpStatusCode.BadRequest =>
                        Failure(QBittorrentApiFailure.BlockedEndpoint),
                    _ => Failure(QBittorrentApiFailure.Rejected)
                };
                response.Dispose();
                throw failure;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw Failure(QBittorrentApiFailure.Cancelled);
            }
            catch (QBittorrentApiException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is HttpRequestException
                    or OperationCanceledException)
            {
                throw Failure(
                    isWrite
                        ? QBittorrentApiFailure.UnknownWriteOutcome
                        : QBittorrentApiFailure.Unavailable);
            }
        }
    }

    private static async Task<string> ReadTextAsync(
        HttpResponseMessage response,
        bool isWrite,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await BoundedHttpContent
                .ReadAsByteArrayAsync(
                    response.Content,
                    MaximumResponseBytes,
                    RequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw Failure(QBittorrentApiFailure.Cancelled);
        }
        catch (InvalidDataException)
        {
            throw Failure(
                isWrite
                    ? QBittorrentApiFailure.UnknownWriteOutcome
                    : QBittorrentApiFailure.Rejected);
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                or IOException
                or OperationCanceledException)
        {
            throw Failure(
                isWrite
                    ? QBittorrentApiFailure.UnknownWriteOutcome
                    : QBittorrentApiFailure.Unavailable);
        }
    }

    private static void ValidateContext(EntryIntegrationProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.PinnedAddresses.Count == 0
            || context.Endpoint.AbsolutePath != "/"
            || context.Endpoint.Scheme is not ("https" or "http"))
        {
            throw Failure(QBittorrentApiFailure.BlockedEndpoint);
        }
    }

    private static string ValidateApiKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 32
            || !value.StartsWith("qbt_", StringComparison.Ordinal)
            || value[4..].Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw Failure(QBittorrentApiFailure.Unauthorized);
        }
        return value;
    }

    private static void ValidateSource(QBittorrentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.InfoHash.Length != 40
            || !source.InfoHash.All(Uri.IsHexDigit)
            || source is QBittorrentFileSource file
                && file.Content.Length is < 4 or > 2 * 1024 * 1024
            || source is QBittorrentMagnetSource magnet
                && (magnet.Magnet.Length > 8192
                    || !magnet.Magnet.StartsWith("magnet:?", StringComparison.Ordinal)))
        {
            throw Failure(QBittorrentApiFailure.Rejected);
        }
    }

    private static void ValidateCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)
            || category.Length > 128
            || category.Any(char.IsControl)
            || !string.Equals(category, category.Trim(), StringComparison.Ordinal))
        {
            throw Failure(QBittorrentApiFailure.Rejected);
        }
    }

    private static QBittorrentApiException Failure(
        QBittorrentApiFailure failure,
        TimeSpan? retryAfter = null) => new(failure, retryAfter);

    private enum TorrentLookupState
    {
        Missing = 0,
        Matching = 1,
        WrongCategory = 2
    }
}
