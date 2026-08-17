using System.Text;
using LenxTool.App.Services;
using LenxTool.Core.Media;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.App.Tests.Services;

public sealed class SubtitleExportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(SubtitleExportMode.OriginalSrt, ".original.srt", "Hello")]
    [InlineData(SubtitleExportMode.TranslatedSrt, ".translated.srt", "你好")]
    [InlineData(SubtitleExportMode.BilingualSrt, ".bilingual.srt", "你好\nHello")]
    [InlineData(SubtitleExportMode.PlainText, ".txt", "你好")]
    public async Task ExportAsyncWritesExpectedUtf8WithoutBom(
        SubtitleExportMode mode,
        string suffix,
        string expectedText)
    {
        var paths = new AppPaths(_root);
        var service = new SubtitleExportService(paths);
        MediaJob job = CreateJob(Path.Combine(_root, "中文 字幕.srt"));
        SubtitleSegment[] segments =
        [
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Hello")
            {
                Sequence = 1,
                TranslatedText = "你好"
            }
        ];

        string output = await service.ExportAsync(job, segments, mode, CancellationToken.None);

        Assert.EndsWith(suffix, output, StringComparison.Ordinal);
        byte[] bytes = await File.ReadAllBytesAsync(output);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Contains(expectedText, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsyncRejectsIncompleteTranslatedOutput()
    {
        var service = new SubtitleExportService(new AppPaths(_root));
        MediaJob job = CreateJob(Path.Combine(_root, "partial.srt"));
        SubtitleSegment[] segments =
        [
            new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello") { Sequence = 1 }
        ];

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(
            job,
            segments,
            SubtitleExportMode.TranslatedSrt,
            CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static MediaJob CreateJob(string inputPath)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(
            "job-export",
            "SubtitleImport",
            inputPath,
            null,
            MediaJobStatus.Completed,
            100,
            TranscriptionEngine.ImportedSrt,
            "deepseek-v4-flash",
            0,
            1,
            null,
            now,
            now);
    }
}
