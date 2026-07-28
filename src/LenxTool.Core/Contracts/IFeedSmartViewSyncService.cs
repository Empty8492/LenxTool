using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedSmartViewSyncService
{
    Task<FeedSmartViewSyncResult> SyncAsync(
        CancellationToken cancellationToken);
}
