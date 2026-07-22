using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedEntryWriter
{
    Task UpsertAsync(
        string feedId,
        IReadOnlyList<FeedEntry> entries,
        CancellationToken cancellationToken);
}
