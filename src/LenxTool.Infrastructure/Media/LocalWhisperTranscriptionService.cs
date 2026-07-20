using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using Whisper.net;

namespace LenxTool.Infrastructure.Media;

public sealed class LocalWhisperTranscriptionService : ILocalTranscriptionService
{
    public async Task<IReadOnlyList<SubtitleSegment>> TranscribeAsync(
        string audioPath,
        string model,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(model) || !Path.GetFileName(model).StartsWith("ggml-", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(new(
                AppErrorCode.InvalidRequest, "本地模型不可用", "请选择已导入的 ggml-*.bin Whisper 模型。",
                "在媒体工作台导入现有模型后重试，模型文件不会上传。"));
        }

        try
        {
            using WhisperFactory factory = WhisperFactory.FromPath(model);
            using WhisperProcessor processor = factory.CreateBuilder().WithLanguage("auto").Build();
            await using FileStream stream = File.OpenRead(audioPath);
            var segments = new List<SubtitleSegment>();
            await foreach (SegmentData result in processor.ProcessAsync(stream, cancellationToken))
            {
                string text = result.Text?.Trim() ?? string.Empty;
                if (text.Length == 0) continue;
                segments.Add(new(result.Start, result.End, text));
                progress?.Report(Math.Min(99, segments.Count));
            }
            progress?.Report(100);
            return segments;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not AppException)
        {
            throw new AppException(new(
                AppErrorCode.Unknown, "本地 Whisper 运行失败", "模型或音频无法由本地识别引擎处理。",
                "确认模型完整且兼容；也可以切换 Groq。", exception.Message, "Whisper.cpp",
                IsRetryable: true), exception);
        }
    }
}
