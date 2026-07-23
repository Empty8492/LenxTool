using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IEntryAssetStore
{
    Task<EntryAsset?> GetAsync(
        string entryId,
        string sourceUrl,
        CancellationToken cancellationToken);

    Task<EntryAsset> PutAsync(
        string entryId,
        string sourceUrl,
        string mimeType,
        Stream content,
        CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(
        EntryAsset asset,
        CancellationToken cancellationToken);

    Task<int> PruneAsync(
        IReadOnlyCollection<string> protectedContentHashes,
        CancellationToken cancellationToken);
}
