using NAudio.Wave;

namespace LenxTool.Infrastructure.Networking;

internal interface IFeedMediaCompatibilityProbe
{
    void EnsureCompatibleVideo(string inputPath);
}

internal sealed class MediaFoundationFeedMediaCompatibilityProbe :
    IFeedMediaCompatibilityProbe
{
    public void EnsureCompatibleVideo(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException(
                "找不到待验证的视频文件。",
                inputPath);
        }

        try
        {
            using var reader = new MediaFoundationReader(inputPath);
            if (reader.WaveFormat.Channels <= 0
                || reader.WaveFormat.SampleRate <= 0)
            {
                throw new InvalidDataException(
                    "视频不包含本机处理链可读取的音轨。");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                "视频与本机媒体处理链不兼容。",
                exception);
        }
    }
}
