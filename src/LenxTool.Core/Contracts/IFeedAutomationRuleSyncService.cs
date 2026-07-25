using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAutomationRuleSyncService
{
    Task<FeedAutomationRuleSyncResult> SyncAsync(
        CancellationToken cancellationToken);
}
