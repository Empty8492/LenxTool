using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IMediaJobRepository
{
    Task UpsertAsync(MediaJob job, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaJob>> GetRecentAsync(int limit, CancellationToken cancellationToken);

    Task<MediaJob?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken) =>
        GetByIdFallbackAsync(this, id, cancellationToken);

    Task<IReadOnlyList<MediaJob>> GetQueuedAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaJob>> RecoverInterruptedAsync(CancellationToken cancellationToken);

    private static async Task<MediaJob?> GetByIdFallbackAsync(
        IMediaJobRepository repository,
        string id,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        IReadOnlyList<MediaJob> recent =
            await repository.GetRecentAsync(500, cancellationToken);
        return recent.FirstOrDefault(
            item => string.Equals(item.Id, id, StringComparison.Ordinal));
    }
}
