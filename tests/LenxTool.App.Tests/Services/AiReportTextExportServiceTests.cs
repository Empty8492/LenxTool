using LenxTool.App.Services;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class AiReportTextExportServiceTests
{
    [Fact]
    public async Task ExportWritesPlainTextMetadataAndContentAtomically()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"LenxTool-AiReportExport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "report.txt");
            var service = new AiReportTextExportService();
            AiReport report = CreateReport();

            await service.ExportAsync(path, report, CancellationToken.None);

            string content = await File.ReadAllTextAsync(path, CancellationToken.None);
            Assert.Contains(report.Title, content, StringComparison.Ordinal);
            Assert.Contains("类型：daily_feed_digest", content, StringComparison.Ordinal);
            Assert.Contains("模型：deepseek-v4-flash", content, StringComparison.Ordinal);
            Assert.Contains(report.Content, content, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("relative.txt")]
    [InlineData("C:\\Temp\\report.html")]
    public async Task ExportRejectsPathsOutsideExplicitTextFileContract(string path)
    {
        var service = new AiReportTextExportService();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.ExportAsync(path, CreateReport(), CancellationToken.None));
    }

    private static AiReport CreateReport() =>
        new(
            $"feed-digest-{new string('a', 64)}",
            "feed_digest",
            FeedDigestScheduleIds.Daily,
            "daily_feed_digest",
            "每日订阅摘要 · 2026-08-06",
            "核心判断：本窗口有两条新增内容。",
            "deepseek-v4-flash",
            1,
            100,
            new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero));
}
