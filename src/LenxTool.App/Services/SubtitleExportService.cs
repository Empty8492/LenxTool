using System.IO;
using System.Text;
using LenxTool.Core.Media;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.App.Services;

public interface ISubtitleExportService
{
    Task<string> ExportAsync(
        MediaJob job,
        IReadOnlyList<SubtitleSegment> segments,
        SubtitleExportMode mode,
        CancellationToken cancellationToken);
}

public sealed class SubtitleExportService(AppPaths paths) : ISubtitleExportService
{
    public async Task<string> ExportAsync(
        MediaJob job,
        IReadOnlyList<SubtitleSegment> segments,
        SubtitleExportMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("当前任务没有可导出的字幕片段。");
        }
        if (mode is not SubtitleExportMode.OriginalSrt &&
            segments.Any(segment => string.IsNullOrWhiteSpace(segment.TranslatedText)))
        {
            throw new InvalidOperationException("译文尚未完整生成，无法导出所选格式。");
        }

        paths.EnsureCreated();
        string baseName = Path.GetFileNameWithoutExtension(job.InputPath);
        string suffix = mode switch
        {
            SubtitleExportMode.OriginalSrt => ".original.srt",
            SubtitleExportMode.TranslatedSrt => ".translated.srt",
            SubtitleExportMode.BilingualSrt => ".bilingual.srt",
            SubtitleExportMode.PlainText => ".txt",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        string outputPath = Path.Combine(paths.OutputDirectory, baseName + suffix);
        string temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            string content = SrtCodec.Export(segments, mode);
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
