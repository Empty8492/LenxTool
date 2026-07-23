using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedFetchStateRepository
{
    Task<FeedRefreshTarget?> GetTargetAsync(
        string feedId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FeedRefreshTarget>> GetAllTargetsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FeedRefreshTarget>> GetDueTargetsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken);

    Task<bool> SaveStateAsync(
        FeedFetchState state,
        CancellationToken cancellationToken);
}
