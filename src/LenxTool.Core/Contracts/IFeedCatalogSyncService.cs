using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedCatalogSyncService
{
    FeedCatalogSyncStatus Current { get; }
    event EventHandler<FeedCatalogSyncStatusChangedEventArgs>? StatusChanged;

    Task InitializeAsync(CancellationToken cancellationToken);
    Task<FeedCatalogSyncResult> SyncAsync(CancellationToken cancellationToken);
}
