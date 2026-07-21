using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface ISubtitleRepository
{
    Task ReplaceAsync(
        string mediaJobId,
        IReadOnlyList<SubtitleSegment> segments,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SubtitleSegment>> GetByMediaJobIdAsync(
        string mediaJobId,
        CancellationToken cancellationToken);
}
