using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFavoriteRepository
{
    Task<int> GetCountAsync(CancellationToken cancellationToken);

    Task<FavoriteItem?> GetAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken);

    Task<FavoriteItem> UpsertAsync(
        string entityType,
        string entityId,
        string note,
        CancellationToken cancellationToken);

    Task<bool> RemoveAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, FavoriteItem>> GetForEntitiesAsync(
        string entityType,
        IReadOnlyCollection<string> entityIds,
        CancellationToken cancellationToken);

    Task<TagItem> UpsertTagAsync(
        string name,
        string color,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TagItem>> GetTagsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TagItem>> GetTagsForEntityAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken);

    Task SetTagsAsync(
        string entityType,
        string entityId,
        IReadOnlyCollection<string> tagIds,
        CancellationToken cancellationToken);

    Task<bool> DeleteTagAsync(
        string tagId,
        CancellationToken cancellationToken);
}
