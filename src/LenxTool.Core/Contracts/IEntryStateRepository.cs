using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IEntryStateRepository
{
    Task<IReadOnlyDictionary<string, EntryState>> GetAsync(
        IReadOnlyCollection<string> entryIds,
        string localProfile,
        CancellationToken cancellationToken);

    Task<EntryState> PatchAsync(
        string entryId,
        string localProfile,
        EntryStatePatch patch,
        CancellationToken cancellationToken);
}
