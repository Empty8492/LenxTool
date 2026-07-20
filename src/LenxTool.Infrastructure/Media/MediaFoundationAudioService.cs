using LenxTool.Core.Contracts;
using NAudio.Wave;

namespace LenxTool.Infrastructure.Media;

public sealed class MediaFoundationAudioService : IMediaAudioService
{
    public Task<PreparedAudio> PrepareAsync(string inputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("找不到要处理的媒体文件。", inputPath);
        return Task.Run(() => Convert(inputPath, cancellationToken), cancellationToken);
    }

    private static PreparedAudio Convert(string inputPath, CancellationToken cancellationToken)
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"lenxtool-{Guid.NewGuid():N}.wav");
        try
        {
            using var reader = new MediaFoundationReader(inputPath);
            using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1))
            {
                ResamplerQuality = 60
            };
            using var writer = new WaveFileWriter(outputPath, resampler.WaveFormat);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(buffer, 0, read);
            }
            return new(outputPath, true, reader.TotalTime);
        }
        catch
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
    }
}
