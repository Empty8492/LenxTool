using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Exports;

public sealed class QBittorrentEntryExporter(
    IIntegrationExportTargetStore<QBittorrentExportTarget> targets,
    IEntryIntegrationPolicyService policies,
    IEntryIntegrationCredentialStore credentials,
    IEntryIntegrationEndpointAuthorizer authorizer,
    ITorrentFileFetcher torrentFiles,
    IQBittorrentApiClient api)
    : IEntryExporter
{
    public const string ExporterId = "qbittorrent";

    public EntryExportCapability Capability { get; } = new(
        ExporterId,
        "qBittorrent",
        Array.AsReadOnly(Enum.GetValues<EntryViewKind>()),
        RequiresCredentials: true,
        MaximumContentBytes: 2 * 1024 * 1024,
        IsIdempotent: true);

    public async Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ExporterId, ExporterId, StringComparison.Ordinal)
            || !QBittorrentExportTarget.IsSupportedQueueTargetId(request.TargetId))
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }
        await using IIntegrationExportTargetLease<QBittorrentExportTarget> lease =
            await targets.AcquireExportLeaseAsync(cancellationToken)
                .ConfigureAwait(false);
        QBittorrentExportTarget target = lease.Target is null
            ? throw Failure(EntryExportErrorCode.Conflict)
            : QBittorrentExportTarget.Normalize(lease.Target);
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
                value.Kind == EntryIntegrationKind.QBittorrent && value.IsEnabled);
        if (policy is null
            || !policy.AllowedResources.Contains(
                target.Category,
                StringComparer.Ordinal))
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        EntryIntegrationProbeContext? context = await authorizer.AuthorizeAsync(
                new(
                    QBittorrentExportTarget.DefaultTargetId,
                    EntryIntegrationKind.QBittorrent,
                    target.Endpoint),
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        string? apiKey = await credentials.GetAsync(
                EntryIntegrationKind.QBittorrent,
                QBittorrentExportTarget.DefaultTargetId,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw Failure(EntryExportErrorCode.CredentialsRequired);
        }
        QBittorrentSource source;
        try
        {
            source = await QBittorrentSourceSelector.SelectAsync(
                    request.Entry,
                    torrentFiles,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EntryExportException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TorrentFileFetchException exception)
        {
            throw exception.Failure switch
            {
                TorrentFileFetchFailure.AccessDenied =>
                    Failure(EntryExportErrorCode.AccessDenied),
                TorrentFileFetchFailure.RateLimited =>
                    Failure(
                        EntryExportErrorCode.RateLimited,
                        true,
                        exception.RetryAfter),
                TorrentFileFetchFailure.Unavailable =>
                    Failure(EntryExportErrorCode.DestinationUnavailable, true),
                _ => Failure(EntryExportErrorCode.UnsupportedContent)
            };
        }
        catch
        {
            throw Failure(EntryExportErrorCode.UnsupportedContent);
        }
        try
        {
            await api.AddAsync(
                    context,
                    apiKey,
                    source,
                    target.Category,
                    cancellationToken)
                .ConfigureAwait(false);
            return EntryExportResult.Success(
                request.IdempotencyKey,
                source.InfoHash,
                null);
        }
        catch (QBittorrentApiException exception)
            when (exception.Failure == QBittorrentApiFailure.Cancelled
                && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (QBittorrentApiException exception)
        {
            throw exception.Failure switch
            {
                QBittorrentApiFailure.Unauthorized
                    or QBittorrentApiFailure.BlockedEndpoint
                    or QBittorrentApiFailure.UnsupportedVersion =>
                    Failure(EntryExportErrorCode.AccessDenied),
                QBittorrentApiFailure.RateLimited =>
                    Failure(
                        EntryExportErrorCode.RateLimited,
                        true,
                        exception.RetryAfter),
                QBittorrentApiFailure.Unavailable
                    or QBittorrentApiFailure.UnknownWriteOutcome
                    or QBittorrentApiFailure.Cancelled =>
                    Failure(EntryExportErrorCode.DestinationUnavailable, true),
                QBittorrentApiFailure.Conflict =>
                    Failure(EntryExportErrorCode.Conflict),
                _ => Failure(EntryExportErrorCode.ProviderRejected)
            };
        }
    }

    private static EntryExportException Failure(
        EntryExportErrorCode code,
        bool retryable = false,
        TimeSpan? retryAfter = null) =>
        new(new(code, retryable, retryAfter));
}
