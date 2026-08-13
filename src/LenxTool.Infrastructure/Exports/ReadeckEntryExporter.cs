using System.Security.Cryptography;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 通过用户已批准的可见技术标签收敛 Readeck 至少一次重放。
/// </summary>
public sealed class ReadeckEntryExporter(
    IIntegrationExportTargetStore<ReadeckExportTarget> targets,
    IEntryIntegrationPolicyService policies,
    IEntryIntegrationCredentialStore credentials,
    IEntryIntegrationEndpointAuthorizer authorizer,
    IReadeckApiClient api)
    : IEntryExporter
{
    public const string ExporterId = "readeck";
    public const int MaximumLabelCount = 64;
    public EntryExportCapability Capability { get; } = new(
        ExporterId,
        "Readeck",
        Array.AsReadOnly(Enum.GetValues<EntryViewKind>()),
        RequiresCredentials: true,
        MaximumContentBytes: 1024,
        IsIdempotent: true);

    public async Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ExporterId, ExporterId, StringComparison.Ordinal)
            || !ReadeckExportTarget.IsSupportedQueueTargetId(request.TargetId))
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        Uri sourceUrl = ValidateSourceUrl(request.Entry.NormalizedUrl);
        await using IIntegrationExportTargetLease<ReadeckExportTarget> lease =
            await targets.AcquireExportLeaseAsync(cancellationToken)
                .ConfigureAwait(false);
        ReadeckExportTarget target = lease.Target is null
            ? throw Failure(EntryExportErrorCode.Conflict)
            : ReadeckExportTarget.Normalize(lease.Target);
        if (!target.MatchesQueueTargetId(request.TargetId))
        {
            throw Failure(EntryExportErrorCode.Conflict);
        }
        if (target.CredentialVersion != 1)
        {
            throw Failure(EntryExportErrorCode.CredentialsRequired);
        }

        EntryIntegrationPolicy? policy = (await policies.GetAsync(
                EntryIntegrationPolicyScope.Active,
                cancellationToken).ConfigureAwait(false))
            .Policies.SingleOrDefault(value =>
                value.Kind == EntryIntegrationKind.Readeck && value.IsEnabled);
        if (policy is null)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        EntryIntegrationProbeContext? context = await authorizer.AuthorizeAsync(
                new(
                    ReadeckExportTarget.DefaultTargetId,
                    EntryIntegrationKind.Readeck,
                    target.Endpoint),
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        string? token = await credentials.GetAsync(
                EntryIntegrationKind.Readeck,
                ReadeckExportTarget.DefaultTargetId,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw Failure(EntryExportErrorCode.CredentialsRequired);
        }
        string technicalLabel = CreateStableLabel(request.Entry.Id);
        string[] labels = NormalizeLabels(
            technicalLabel,
            request.Entry.Categories);
        var bookmark = new ReadeckBookmark(
            technicalLabel,
            sourceUrl,
            NormalizeTitle(request.Entry.Title),
            labels,
            target.Archive);
        try
        {
            ReadeckBookmarkResult result = await api.UpsertAsync(
                    context,
                    token,
                    bookmark,
                    cancellationToken)
                .ConfigureAwait(false);
            return EntryExportResult.Success(
                request.IdempotencyKey,
                result.Id,
                result.Url);
        }
        catch (ReadeckApiException exception)
            when (exception.Failure == ReadeckApiFailure.Cancelled
                && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ReadeckApiException exception)
        {
            throw exception.Failure switch
            {
                ReadeckApiFailure.Unauthorized
                    or ReadeckApiFailure.BlockedEndpoint =>
                    Failure(EntryExportErrorCode.AccessDenied),
                ReadeckApiFailure.RateLimited =>
                    Failure(
                        EntryExportErrorCode.RateLimited,
                        true,
                        exception.RetryAfter),
                ReadeckApiFailure.Unavailable
                    or ReadeckApiFailure.UnknownWriteOutcome
                    or ReadeckApiFailure.Cancelled =>
                    Failure(EntryExportErrorCode.DestinationUnavailable, true),
                ReadeckApiFailure.Conflict =>
                    Failure(EntryExportErrorCode.Conflict),
                _ => Failure(EntryExportErrorCode.ProviderRejected)
            };
        }
    }

    public static string CreateStableLabel(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(entryId)))
            .ToLowerInvariant()[..24];
        return $"lenxtool:{hash}";
    }

    private static string[] NormalizeLabels(
        string technicalLabel,
        IReadOnlyList<string> categories)
    {
        var labels = new SortedSet<string>(StringComparer.Ordinal)
        {
            technicalLabel
        };
        foreach (string value in categories ?? [])
        {
            string label = string.Join(
                ' ',
                (value ?? string.Empty).Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));
            if (label.Length is > 0 and <= 128
                && !label.Any(char.IsControl))
            {
                labels.Add(label);
            }
            if (labels.Count >= MaximumLabelCount) break;
        }
        return labels.ToArray();
    }

    private static Uri ValidateSourceUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsoluteUri.Length > 1024)
        {
            throw Failure(EntryExportErrorCode.UnsupportedContent);
        }
        return uri;
    }

    private static string NormalizeTitle(string value)
    {
        string title = string.Join(
            ' ',
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) return "无标题条目";
        return title.Length <= 1024 ? title : title[..1024];
    }

    private static EntryExportException Failure(
        EntryExportErrorCode code,
        bool retryable = false,
        TimeSpan? retryAfter = null) =>
        new(new(code, retryable, retryAfter));
}
