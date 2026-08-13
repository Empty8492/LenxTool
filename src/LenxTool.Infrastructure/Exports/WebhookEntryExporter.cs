using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Exports;

public sealed class WebhookEntryExporter(
    IIntegrationExportTargetStore<WebhookExportTarget> targets,
    IEntryIntegrationPolicyService policies,
    IEntryIntegrationCredentialStore credentials,
    IEntryIntegrationEndpointAuthorizer authorizer,
    IWebhookApiClient api)
    : IEntryExporter
{
    public const string ExporterId = "webhook";
    public const int MaximumSummaryBytes = 16 * 1024;

    public EntryExportCapability Capability { get; } = new(
        ExporterId,
        "受控 Webhook",
        Array.AsReadOnly(Enum.GetValues<EntryViewKind>()),
        RequiresCredentials: false,
        MaximumContentBytes: 32 * 1024,
        IsIdempotent: true);

    public async Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ExporterId, ExporterId, StringComparison.Ordinal)
            || !WebhookExportTarget.IsSupportedQueueTargetId(request.TargetId))
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        await using IIntegrationExportTargetLease<WebhookExportTarget> lease =
            await targets.AcquireExportLeaseAsync(cancellationToken)
                .ConfigureAwait(false);
        WebhookExportTarget target = lease.Target is null
            ? throw Failure(EntryExportErrorCode.Conflict)
            : WebhookExportTarget.Normalize(lease.Target);
        if (!target.MatchesQueueTargetId(request.TargetId))
        {
            throw Failure(EntryExportErrorCode.Conflict);
        }
        if (target.UseHmac && target.CredentialVersion != 1)
        {
            throw Failure(EntryExportErrorCode.CredentialsRequired);
        }
        EntryIntegrationPolicy? policy = (await policies.GetAsync(
                EntryIntegrationPolicyScope.Active,
                cancellationToken).ConfigureAwait(false))
            .Policies.SingleOrDefault(value =>
                value.Kind == EntryIntegrationKind.Webhook && value.IsEnabled);
        if (policy is null)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        EntryIntegrationProbeContext? context = await authorizer.AuthorizeAsync(
                new(
                    WebhookExportTarget.DefaultTargetId,
                    EntryIntegrationKind.Webhook,
                    target.Endpoint),
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        string? secret = null;
        if (target.UseHmac)
        {
            secret = await credentials.GetAsync(
                    EntryIntegrationKind.Webhook,
                    WebhookExportTarget.DefaultTargetId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw Failure(EntryExportErrorCode.CredentialsRequired);
            }
        }

        var payload = new WebhookEntryPayload(
            request.IdempotencyKey,
            request.Entry.Id,
            NormalizeText(request.Entry.Title, 1024),
            NormalizeUrl(request.Entry.NormalizedUrl),
            NormalizeOptional(request.Entry.Author, 1024),
            request.Entry.PublishedAt,
            TruncateUtf8(request.Entry.Summary, MaximumSummaryBytes),
            NormalizeCategories(request.Entry.Categories),
            request.ViewKind);
        try
        {
            await api.ProbeAsync(context, cancellationToken).ConfigureAwait(false);
            await api.SendAsync(context, secret, payload, cancellationToken)
                .ConfigureAwait(false);
            return EntryExportResult.Success(
                request.IdempotencyKey,
                request.IdempotencyKey,
                null);
        }
        catch (WebhookApiException exception)
            when (exception.Failure == WebhookApiFailure.Cancelled
                && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (WebhookApiException exception)
        {
            throw exception.Failure switch
            {
                WebhookApiFailure.BlockedEndpoint
                    or WebhookApiFailure.CapabilityMissing =>
                    Failure(EntryExportErrorCode.AccessDenied),
                WebhookApiFailure.RateLimited =>
                    Failure(
                        EntryExportErrorCode.RateLimited,
                        true,
                        exception.RetryAfter),
                WebhookApiFailure.Unavailable
                    or WebhookApiFailure.UnknownWriteOutcome
                    or WebhookApiFailure.Cancelled =>
                    Failure(EntryExportErrorCode.DestinationUnavailable, true),
                _ => Failure(EntryExportErrorCode.ProviderRejected)
            };
        }
    }

    private static string[] NormalizeCategories(
        IReadOnlyList<string> values) =>
        values
            .Select(value => NormalizeText(value, 128))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(64)
            .ToArray();

    private static Uri? NormalizeUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? url)
        && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(url.UserInfo)
        && url.AbsoluteUri.Length <= 2048
            ? url
            : null;

    private static string? NormalizeOptional(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : NormalizeText(value, maximum);

    private static string NormalizeText(string? value, int maximum)
    {
        string normalized = string.Join(
            ' ',
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximum
            ? normalized
            : normalized[..maximum];
    }

    private static string TruncateUtf8(string? value, int maximumBytes)
    {
        string text = value ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) <= maximumBytes)
        {
            return text;
        }

        int utf8Bytes = 0;
        int utf16Length = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (utf8Bytes + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }
            utf8Bytes += rune.Utf8SequenceLength;
            utf16Length += rune.Utf16SequenceLength;
        }
        return text[..utf16Length];
    }

    private static EntryExportException Failure(
        EntryExportErrorCode code,
        bool retryable = false,
        TimeSpan? retryAfter = null) =>
        new(new(code, retryable, retryAfter));
}
