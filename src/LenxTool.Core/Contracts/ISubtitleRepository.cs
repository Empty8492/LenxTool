using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface ISubtitleRepository
{
    Task CreateMediaJobWithSegmentsAsync(
        MediaJob job,
        IReadOnlyList<SubtitleSegment> segments,
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        string mediaJobId,
        IReadOnlyList<SubtitleSegment> segments,
        CancellationToken cancellationToken);

    Task SaveTranslationBatchAsync(
        MediaJob job,
        IReadOnlyList<SubtitleSegment> segments,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SubtitleSegment>> GetByMediaJobIdAsync(
        string mediaJobId,
        CancellationToken cancellationToken);
}
