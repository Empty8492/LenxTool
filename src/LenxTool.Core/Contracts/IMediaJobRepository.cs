using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IMediaJobRepository
{
    Task UpsertAsync(MediaJob job, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaJob>> GetRecentAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaJob>> GetQueuedAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaJob>> RecoverInterruptedAsync(CancellationToken cancellationToken);
}
