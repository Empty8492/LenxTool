using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// 固定 Webhook v1 协议；目标必须先声明幂等能力，写入后必须回显稳定 ack。
/// </summary>
internal sealed class WebhookApiClient : IWebhookApiClient
{
    private const int MaximumRequestBytes = 64 * 1024;
    private const int MaximumResponseBytes = 4 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 8
        };
    private readonly IIntegrationHttpClientFactory _clients;

    public WebhookApiClient()
        : this(new PinnedIntegrationHttpClientFactory())
    {
    }

    internal WebhookApiClient(IIntegrationHttpClientFactory clients)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
    }

    public async Task ProbeAsync(
        EntryIntegrationProbeContext context,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        using HttpClient client = _clients.Create(context);
        using var request = new HttpRequestMessage(
            HttpMethod.Options,
            context.Endpoint);
        using HttpResponseMessage response = await SendCoreAsync(
                client,
                request,
                isWrite: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (!HasSingleHeader(response, "LenxTool-Webhook-Version", "1")
            || !HasSingleHeader(
                response,
                "LenxTool-Idempotency",
                "required"))
        {
            throw Failure(WebhookApiFailure.CapabilityMissing);
        }
        await DrainBoundedAsync(response, isWrite: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SendAsync(
        EntryIntegrationProbeContext context,
        string? hmacSecret,
        WebhookEntryPayload payload,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        ValidatePayload(payload);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                @event = "entry.exported",
                eventId = payload.EventId,
                entry = new
                {
                    id = payload.EntryId,
                    title = payload.Title,
                    url = payload.Url?.AbsoluteUri,
                    author = payload.Author,
                    publishedAt = payload.PublishedAt,
                    summary = payload.Summary,
                    categories = payload.Categories
                },
                viewKind = payload.ViewKind.ToString()
            },
            JsonOptions);
        if (body.Length > MaximumRequestBytes)
        {
            throw Failure(WebhookApiFailure.Rejected);
        }
        using HttpClient client = _clients.Create(context);
        using var request = new HttpRequestMessage(HttpMethod.Post, context.Endpoint);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            payload.EventId);
        request.Headers.TryAddWithoutValidation(
            "X-LenxTool-Event",
            "entry.exported");
        if (hmacSecret is not null)
        {
            string secret = ValidateSecret(hmacSecret);
            string signature = Convert.ToHexString(
                    HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body))
                .ToLowerInvariant();
            request.Headers.TryAddWithoutValidation(
                "X-LenxTool-Signature",
                $"sha256={signature}");
        }
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
        using HttpResponseMessage response = await SendCoreAsync(
                client,
                request,
                isWrite: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!HasSingleHeader(response, "LenxTool-Ack", payload.EventId))
        {
            throw Failure(WebhookApiFailure.UnknownWriteOutcome);
        }
        await DrainBoundedAsync(response, isWrite: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendCoreAsync(
        HttpClient client,
        HttpRequestMessage request,
        bool isWrite,
        CancellationToken cancellationToken)
    {
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
            if (response.IsSuccessStatusCode) return response;
            WebhookApiException failure = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests =>
                    Failure(
                        WebhookApiFailure.RateLimited,
                        response.Headers.RetryAfter?.Delta),
                >= HttpStatusCode.InternalServerError =>
                    Failure(WebhookApiFailure.Unavailable),
                >= HttpStatusCode.MultipleChoices
                    and < HttpStatusCode.BadRequest =>
                    Failure(WebhookApiFailure.BlockedEndpoint),
                _ => Failure(WebhookApiFailure.Rejected)
            };
            response.Dispose();
            throw failure;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw Failure(WebhookApiFailure.Cancelled);
        }
        catch (WebhookApiException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                or OperationCanceledException)
        {
            throw Failure(
                isWrite
                    ? WebhookApiFailure.UnknownWriteOutcome
                    : WebhookApiFailure.Unavailable);
        }
    }

    private static async Task DrainBoundedAsync(
        HttpResponseMessage response,
        bool isWrite,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await BoundedHttpContent
                .ReadAsByteArrayAsync(
                    response.Content,
                    MaximumResponseBytes,
                    RequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WebhookApiException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw Failure(WebhookApiFailure.Cancelled);
        }
        catch (InvalidDataException)
        {
            throw Failure(
                isWrite
                    ? WebhookApiFailure.UnknownWriteOutcome
                    : WebhookApiFailure.CapabilityMissing);
        }
        catch
        {
            throw Failure(
                isWrite
                    ? WebhookApiFailure.UnknownWriteOutcome
                    : WebhookApiFailure.Unavailable);
        }
    }

    private static bool HasSingleHeader(
        HttpResponseMessage response,
        string name,
        string expected)
    {
        if (!response.Headers.TryGetValues(
                name,
                out IEnumerable<string>? values))
        {
            return false;
        }
        using IEnumerator<string> enumerator = values.GetEnumerator();
        return enumerator.MoveNext()
            && string.Equals(
                enumerator.Current,
                expected,
                StringComparison.Ordinal)
            && !enumerator.MoveNext();
    }

    private static void ValidateContext(EntryIntegrationProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Endpoint.Scheme != Uri.UriSchemeHttps
            || context.PinnedAddresses.Count == 0
            || !string.IsNullOrEmpty(context.Endpoint.Query)
            || !string.IsNullOrEmpty(context.Endpoint.Fragment))
        {
            throw Failure(WebhookApiFailure.BlockedEndpoint);
        }
    }

    private static void ValidatePayload(WebhookEntryPayload value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.EventId.Length != 64
            || value.EventId.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))
            || string.IsNullOrWhiteSpace(value.EntryId)
            || value.EntryId.Length > 512
            || value.Title.Length > 1024
            || value.Summary.Length > 32 * 1024
            || value.Categories.Count > 64
            || !Enum.IsDefined(value.ViewKind))
        {
            throw Failure(WebhookApiFailure.Rejected);
        }
    }

    private static string ValidateSecret(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 4096
            || value.Any(char.IsControl)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Failure(WebhookApiFailure.Rejected);
        }
        return value;
    }

    private static WebhookApiException Failure(
        WebhookApiFailure failure,
        TimeSpan? retryAfter = null) => new(failure, retryAfter);
}
