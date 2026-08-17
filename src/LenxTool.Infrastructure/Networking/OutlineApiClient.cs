using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// Outline API 的最小、确定性文档写入客户端。调用方必须先完成策略与 DNS pin。
/// </summary>
internal sealed class OutlineApiClient : IOutlineApiClient
{
    private const int MaximumCredentialLength = 2048;
    private const int MaximumRequestBytes = 128 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private readonly IIntegrationHttpClientFactory _clients;

    public OutlineApiClient()
        : this(new PinnedIntegrationHttpClientFactory())
    {
    }

    internal OutlineApiClient(IIntegrationHttpClientFactory clients)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
    }

    public async Task<OutlineCapability> ProbeAsync(
        EntryIntegrationProbeContext context,
        string token,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        string credential = ValidateCredential(token);
        using HttpClient client = _clients.Create(context);
        using HttpResponseMessage response = await SendAsync(
                client,
                context,
                credential,
                "api/auth.info",
                new { },
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument json = await ReadJsonAsync(
                response,
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureOk(json.RootElement, isWrite: false);
        string version = TryGetString(
                json.RootElement,
                "data",
                "version")
            ?? "supported";
        return new(version);
    }

    public async Task<OutlineDocumentResult> UpsertAsync(
        EntryIntegrationProbeContext context,
        string token,
        OutlineDocument document,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        ValidateDocument(document);
        string credential = ValidateCredential(token);
        using HttpClient client = _clients.Create(context);

        bool exists = await ExistsAsync(
                client,
                context,
                credential,
                document.Id,
                document.CollectionId,
                cancellationToken)
            .ConfigureAwait(false);
        string path = exists
            ? "api/documents.update"
            : "api/documents.create";
        object payload = new
        {
            id = document.Id.ToString("D"),
            collectionId = document.CollectionId.ToString("D"),
            title = document.Title,
            text = document.Text,
            publish = document.Publish
        };
        using HttpResponseMessage response = await SendAsync(
                client,
                context,
                credential,
                path,
                payload,
                isWrite: true,
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument json = await ReadJsonAsync(
                response,
                isWrite: true,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureOk(json.RootElement, isWrite: true);
        JsonElement data = GetRequiredObject(
            json.RootElement,
            "data",
            OutlineApiFailure.UnknownWriteOutcome);
        Guid returnedId = GetRequiredGuid(
            data,
            "id",
            OutlineApiFailure.UnknownWriteOutcome);
        if (returnedId != document.Id)
        {
            throw Failure(OutlineApiFailure.UnknownWriteOutcome);
        }
        Guid returnedCollectionId = GetRequiredGuid(
            data,
            "collectionId",
            OutlineApiFailure.UnknownWriteOutcome);
        if (returnedCollectionId != document.CollectionId)
        {
            throw Failure(OutlineApiFailure.Conflict);
        }
        string rawUrl = GetRequiredString(
            data,
            "url",
            OutlineApiFailure.UnknownWriteOutcome);
        Uri resultUrl = new(context.Endpoint, rawUrl);
        if (!IsSameAuthority(context.Endpoint, resultUrl))
        {
            throw Failure(OutlineApiFailure.UnknownWriteOutcome);
        }
        return new(returnedId, resultUrl);
    }

    private static async Task<bool> ExistsAsync(
        HttpClient client,
        EntryIntegrationProbeContext context,
        string token,
        Guid id,
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
                client,
                context,
                token,
                "api/documents.info",
                new { id = id.ToString("D") },
                isWrite: false,
                cancellationToken,
                allowNotFound: true)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        using JsonDocument json = await ReadJsonAsync(
                response,
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureOk(json.RootElement, isWrite: false);
        JsonElement data = GetRequiredObject(
            json.RootElement,
            "data",
            OutlineApiFailure.Rejected);
        Guid returnedId = GetRequiredGuid(
            data,
            "id",
            OutlineApiFailure.Rejected);
        if (returnedId != id)
        {
            throw Failure(OutlineApiFailure.Rejected);
        }
        Guid returnedCollectionId = GetRequiredGuid(
            data,
            "collectionId",
            OutlineApiFailure.Rejected);
        if (returnedCollectionId != collectionId)
        {
            // documents.update accepts collectionId and would otherwise move a
            // deterministic document out of a collection that is no longer
            // authorized for this target.
            throw Failure(OutlineApiFailure.Conflict);
        }
        return true;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        EntryIntegrationProbeContext context,
        string token,
        string relativePath,
        object payload,
        bool isWrite,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (body.Length > MaximumRequestBytes)
        {
            throw Failure(OutlineApiFailure.Rejected);
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(context.Endpoint, relativePath));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (allowNotFound
                && response.StatusCode == HttpStatusCode.NotFound)
            {
                return response;
            }
            if (!response.IsSuccessStatusCode)
            {
                OutlineApiException failure = MapStatus(response);
                response.Dispose();
                throw failure;
            }
            return response;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw Failure(OutlineApiFailure.Cancelled);
        }
        catch (OutlineApiException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                or OperationCanceledException)
        {
            throw Failure(
                isWrite
                    ? OutlineApiFailure.UnknownWriteOutcome
                    : OutlineApiFailure.Unavailable);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        bool isWrite,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await BoundedHttpContent
                .ReadAsByteArrayAsync(
                    response.Content,
                    MaximumResponseBytes,
                    RequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw Failure(OutlineApiFailure.Cancelled);
        }
        catch (InvalidDataException)
        {
            throw Failure(
                isWrite
                    ? OutlineApiFailure.UnknownWriteOutcome
                    : OutlineApiFailure.Rejected);
        }
        catch
        {
            throw Failure(
                isWrite
                    ? OutlineApiFailure.UnknownWriteOutcome
                    : OutlineApiFailure.Unavailable);
        }
        if (bytes.Length == 0)
        {
            throw Failure(
                isWrite
                    ? OutlineApiFailure.UnknownWriteOutcome
                    : OutlineApiFailure.Rejected);
        }
        try
        {
            return JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw Failure(
                isWrite
                    ? OutlineApiFailure.UnknownWriteOutcome
                    : OutlineApiFailure.Rejected);
        }
    }

    private static OutlineApiException MapStatus(
        HttpResponseMessage response) =>
        response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                Failure(OutlineApiFailure.Unauthorized),
            HttpStatusCode.TooManyRequests =>
                Failure(
                    OutlineApiFailure.RateLimited,
                    response.Headers.RetryAfter?.Delta),
            >= HttpStatusCode.InternalServerError =>
                Failure(OutlineApiFailure.Unavailable),
            >= HttpStatusCode.MultipleChoices
                and < HttpStatusCode.BadRequest =>
                Failure(OutlineApiFailure.BlockedEndpoint),
            _ => Failure(OutlineApiFailure.Rejected)
        };

    private static void ValidateContext(EntryIntegrationProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Endpoint.AbsolutePath != "/"
            || context.PinnedAddresses.Count == 0)
        {
            throw Failure(OutlineApiFailure.BlockedEndpoint);
        }
    }

    private static string ValidateCredential(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumCredentialLength
            || value.Any(char.IsControl)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Failure(OutlineApiFailure.Unauthorized);
        }
        return value;
    }

    private static void ValidateDocument(OutlineDocument value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Id == Guid.Empty
            || value.CollectionId == Guid.Empty
            || string.IsNullOrWhiteSpace(value.Title)
            || value.Title.Length > 1024
            || Encoding.UTF8.GetByteCount(value.Text) > 64 * 1024)
        {
            throw Failure(OutlineApiFailure.Rejected);
        }
    }

    private static void EnsureOk(JsonElement root, bool isWrite)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("ok", out JsonElement ok)
            || ok.ValueKind != JsonValueKind.True)
        {
            throw Failure(
                isWrite
                    ? OutlineApiFailure.UnknownWriteOutcome
                    : OutlineApiFailure.Rejected);
        }
    }

    private static JsonElement GetRequiredObject(
        JsonElement value,
        string name,
        OutlineApiFailure failure)
    {
        if (!value.TryGetProperty(name, out JsonElement result)
            || result.ValueKind != JsonValueKind.Object)
        {
            throw Failure(failure);
        }
        return result;
    }

    private static string GetRequiredString(
        JsonElement value,
        string name,
        OutlineApiFailure failure)
    {
        if (!value.TryGetProperty(name, out JsonElement result)
            || result.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(result.GetString()))
        {
            throw Failure(failure);
        }
        return result.GetString()!;
    }

    private static Guid GetRequiredGuid(
        JsonElement value,
        string name,
        OutlineApiFailure failure)
    {
        string raw = GetRequiredString(value, name, failure);
        if (!Guid.TryParse(raw, out Guid result)
            || result == Guid.Empty)
        {
            throw Failure(failure);
        }
        return result;
    }

    private static string? TryGetString(
        JsonElement value,
        string objectName,
        string propertyName) =>
        value.TryGetProperty(objectName, out JsonElement nested)
        && nested.ValueKind == JsonValueKind.Object
        && nested.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsSameAuthority(Uri left, Uri right) =>
        right.IsAbsoluteUri
        && string.Equals(left.Scheme, right.Scheme, StringComparison.Ordinal)
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.Ordinal)
        && left.Port == right.Port
        && string.IsNullOrEmpty(right.UserInfo);

    private static OutlineApiException Failure(
        OutlineApiFailure failure,
        TimeSpan? retryAfter = null) =>
        new(
            failure,
            failure is OutlineApiFailure.RateLimited
                or OutlineApiFailure.Unavailable
                or OutlineApiFailure.UnknownWriteOutcome,
            retryAfter);
}
