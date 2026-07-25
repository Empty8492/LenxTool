using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedEntryRepository : IFeedEntryWriter
{
    Task<FeedEntry?> GetByIdAsync(
        string entryId,
        CancellationToken cancellationToken);

    Task<FeedEntryPage> QueryAsync(
        FeedEntryQuery query,
        CancellationToken cancellationToken);

    Task<int> DeleteExpiredUnprotectedAsync(
        DateTimeOffset cutoff,
        int maximumCount,
        CancellationToken cancellationToken);
}
