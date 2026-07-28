using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedSmartViewRepository
{
    Task<FeedSmartViewSnapshot> GetAsync(
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        FeedSmartViewSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<bool> MarkSynchronizedAsync(
        long expectedVersion,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken);
}
