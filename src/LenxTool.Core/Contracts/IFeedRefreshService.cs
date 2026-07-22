using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedRefreshService
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<FeedRefreshResult> RefreshAsync(
        string feedId,
        bool force,
        CancellationToken cancellationToken);

    Task<FeedRefreshBatchResult> RefreshDueAsync(CancellationToken cancellationToken);
}
