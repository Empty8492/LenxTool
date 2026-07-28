using System.Globalization;
using System.Net;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class FeedSmartViewSyncService(
    WorkerAccountSessionService accountSession,
    IFeedSmartViewRepository repository,
    TimeProvider timeProvider)
    : IFeedSmartViewSyncService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public async Task<FeedSmartViewSyncResult> SyncAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!accountSession.Current.IsAuthenticated)
        {
            return new(
                FeedSmartViewSyncOutcome.SkippedNotAuthenticated,
                0,
                null);
        }
        await _gate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            FeedSmartViewSnapshot local =
                await repository.GetAsync(cancellationToken)
                    .ConfigureAwait(false);
            string path = string.Create(
                CultureInfo.InvariantCulture,
                $"/v1/smart-views?scope=ACTIVE&afterVersion={local.ViewSetVersion}");
            using HttpResponseMessage response =
                await accountSession.GetAuthorizedAsync(
                    path,
                    cancellationToken).ConfigureAwait(false);
            DateTimeOffset synchronizedAt = timeProvider.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (local.ViewSetVersion == 0 &&
                    local.LastSyncedAt is null)
                {
                    await repository.ReplaceAsync(
                        local with
                        {
                            LastSyncedAt = synchronizedAt
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else if (!await repository.MarkSynchronizedAsync(
                    local.ViewSetVersion,
                    synchronizedAt,
                    cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "同步期间本地智能视图版本发生变化。");
                }
                return new(
                    FeedSmartViewSyncOutcome.Unchanged,
                    local.ViewSetVersion,
                    synchronizedAt);
            }
            await WorkerAccountSessionService.EnsureSuccessAsync(
                response,
                cancellationToken).ConfigureAwait(false);
            FeedSmartViewWireProtocol.SnapshotDto dto =
                await FeedSmartViewWireProtocol.ReadAsync<
                    FeedSmartViewWireProtocol.SnapshotDto>(
                    response,
                    cancellationToken).ConfigureAwait(false);
            FeedSmartViewSnapshot snapshot;
            try
            {
                snapshot = FeedSmartViewWireProtocol.MapSnapshot(
                    dto,
                    FeedSmartViewScope.Active,
                    synchronizedAt,
                    local.ViewSetVersion);
            }
            catch (AppException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    or InvalidDataException
                    or InvalidOperationException)
            {
                throw new AppException(
                    FeedSmartViewWireProtocol.InvalidResponse().Error,
                    exception);
            }
            await repository.ReplaceAsync(
                snapshot,
                cancellationToken).ConfigureAwait(false);
            return new(
                FeedSmartViewSyncOutcome.Updated,
                snapshot.ViewSetVersion,
                synchronizedAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Dispose();
    }
}
