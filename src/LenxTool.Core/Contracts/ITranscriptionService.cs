using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface ITranscriptionService
{
    Task<IReadOnlyList<SubtitleSegment>> TranscribeAsync(
        string audioPath,
        string model,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public interface ILocalTranscriptionService : ITranscriptionService
{
}

public sealed record PreparedAudio(string Path, bool IsTemporary, TimeSpan? Duration);

public interface IMediaAudioService
{
    Task<PreparedAudio> PrepareAsync(string inputPath, CancellationToken cancellationToken);
}
