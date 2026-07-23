using System.Globalization;
using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Media;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class HistoryViewModelTests
{
    [Fact]
    public async Task SearchCommandDisplaysUnifiedContentResults()
    {
        var expected = new ContentSearchResult(
            "news-1",
            ContentSearchResultType.News,
            "人工智能进入本地时代",
            "本地模型与云端服务开始协同。",
            "Lenx 早报",
            "https://example.test/news/1",
            DateTimeOffset.Parse("2026-07-20T08:00:00+08:00", CultureInfo.InvariantCulture));
        var viewModel = new HistoryViewModel(
            new StubMediaJobRepository(),
            new StubDatabaseMaintenanceService(),
            new StubDialogs(),
            new StubNewsRepository([expected]),
            new StubMediaJobRepository(),
            new StubSubtitleExportService());
        viewModel.SearchQuery = "人工智能";

        await viewModel.SearchCommand.ExecuteAsync();

        Assert.Same(expected, Assert.Single(viewModel.SearchResults));
        Assert.Equal("找到 1 条相关内容。", viewModel.SearchStatus);
        Assert.Same(expected, viewModel.SelectedSearchResult);
    }

    [Fact]
    public async Task SearchCommandDoesNotShowLegacyAndFeedCopiesOfOneUrl()
    {
        var legacy = new ContentSearchResult(
            "legacy",
            ContentSearchResultType.News,
            "同一条早报",
            "旧表记录",
            "AI 早报",
            "https://daily.juya.uk/article/1",
            DateTimeOffset.UtcNow.AddMinutes(-2));
        var feed = legacy with
        {
            EntityId = "feed-entry",
            Type = ContentSearchResultType.FeedEntry,
            Source = "Daily Feed",
            Summary = "Feed 条目"
        };
        var viewModel = new HistoryViewModel(
            new StubMediaJobRepository(),
            new StubDatabaseMaintenanceService(),
            new StubDialogs(),
            new StubNewsRepository([legacy, feed]),
            new StubMediaJobRepository(),
            new StubSubtitleExportService())
        {
            SearchQuery = "早报"
        };

        await viewModel.SearchCommand.ExecuteAsync();

        Assert.Single(viewModel.SearchResults);
        Assert.Equal("legacy", viewModel.SearchResults[0].EntityId);
    }

    [Fact]
    public void SearchCommandRequiresNonWhitespaceQuery()
    {
        var viewModel = new HistoryViewModel(
            new StubMediaJobRepository(),
            new StubDatabaseMaintenanceService(),
            new StubDialogs(),
            new StubNewsRepository([]),
            new StubMediaJobRepository(),
            new StubSubtitleExportService());

        viewModel.SearchQuery = "   ";

        Assert.False(viewModel.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectingSubtitleJobLoadsSegmentsUsageAndSupportsLocalReExport()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var job = new MediaJob(
            "subtitle-history",
            "SubtitleImport",
            "D:\\字幕\\history.srt",
            null,
            MediaJobStatus.Failed,
            50,
            TranscriptionEngine.ImportedSrt,
            "deepseek-v4-flash",
            0,
            2,
            new(
                AppErrorCode.ProviderUnavailable,
                "翻译服务暂时不可用",
                "翻译已在断点处停止。",
                "稍后继续翻译。",
                "Bearer secret-must-not-be-shown"),
            now,
            now)
        {
            TranslationProvider = "DeepSeek",
            TranslationTargetLanguage = "简体中文",
            TranslationNextSegmentIndex = 1,
            TranslationPromptTokens = 100,
            TranslationCompletionTokens = 30,
            TranslationTotalTokens = 130
        };
        SubtitleSegment[] segments =
        [
            new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello", "你好") { Sequence = 1 }
        ];
        var repository = new StubMediaJobRepository([job], segments);
        var exporter = new StubSubtitleExportService();
        var viewModel = new HistoryViewModel(
            repository,
            new StubDatabaseMaintenanceService(),
            new StubDialogs(),
            new StubNewsRepository([]),
            repository,
            exporter);

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.SelectedJobLoad;

        Assert.Equal(segments, viewModel.SubtitleSegments);
        Assert.Equal("DeepSeek · deepseek-v4-flash", viewModel.ProviderSummary);
        Assert.Equal("2 次请求 · 130 tokens（输入 100 / 输出 30）", viewModel.UsageSummary);
        Assert.Equal("翻译已在断点处停止。", viewModel.ErrorSummary);
        Assert.DoesNotContain("secret", viewModel.ErrorSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.SelectedExportOption = viewModel.ExportOptions.Single(
            option => option.Mode == SubtitleExportMode.TranslatedSrt);
        await viewModel.ExportSubtitleCommand.ExecuteAsync();

        Assert.Equal(SubtitleExportMode.TranslatedSrt, exporter.Mode);
        Assert.Equal(job.Id, exporter.Job?.Id);
    }

    private sealed class StubNewsRepository(IReadOnlyList<ContentSearchResult> results) : INewsRepository
    {
        public Task<IReadOnlyList<ContentSearchResult>> SearchContentAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(results);

        public Task UpsertReportAsync(AiReport report, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AiReport>> GetLatestReportsAsync(
            int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AiReport>>([]);

        public Task UpsertAsync(IReadOnlyCollection<NewsArticle> articles, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<NewsArticle>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NewsArticle>>([]);

        public Task<IReadOnlyList<NewsArticle>> GetLatestAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NewsArticle>>([]);

        public Task UpsertTrendsAsync(IReadOnlyCollection<TrendItem> trends, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<TrendItem>> GetLatestTrendsAsync(int limit, string? platform, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TrendItem>>([]);
    }

    private sealed class StubMediaJobRepository(
        IReadOnlyList<MediaJob>? jobs = null,
        IReadOnlyList<SubtitleSegment>? segments = null) : IMediaJobRepository, ISubtitleRepository
    {
        public Task UpsertAsync(MediaJob job, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MediaJob>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult(jobs ?? []);
        public Task<IReadOnlyList<MediaJob>> GetQueuedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaJob>>([]);
        public Task<IReadOnlyList<MediaJob>> RecoverInterruptedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaJob>>([]);
        public Task CreateMediaJobWithSegmentsAsync(
            MediaJob job,
            IReadOnlyList<SubtitleSegment> subtitleSegments,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReplaceAsync(
            string mediaJobId,
            IReadOnlyList<SubtitleSegment> subtitleSegments,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveTranslationBatchAsync(
            MediaJob job,
            IReadOnlyList<SubtitleSegment> subtitleSegments,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SubtitleSegment>> GetByMediaJobIdAsync(
            string mediaJobId,
            CancellationToken cancellationToken) => Task.FromResult(segments ?? []);
    }

    private sealed class StubDatabaseMaintenanceService : IDatabaseMaintenanceService
    {
        public Task<string> BackupAsync(string? destinationPath, CancellationToken cancellationToken) =>
            Task.FromResult("backup.db");
        public Task RestoreAsync(string sourcePath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubDialogs : IDesktopFileDialogService
    {
        public IReadOnlyList<string> PickMediaFiles() => [];
        public string? PickWhisperModel() => null;
        public string? PickDatabaseBackup() => null;
        public string? PickFileForHash() => null;
        public (string Source, string Destination)? PickWordConversion() => null;
        public void OpenFolder(string path) { }
        public void OpenUri(string uri) { }
    }

    private sealed class StubSubtitleExportService : ISubtitleExportService
    {
        public MediaJob? Job { get; private set; }
        public SubtitleExportMode? Mode { get; private set; }

        public Task<string> ExportAsync(
            MediaJob job,
            IReadOnlyList<SubtitleSegment> segments,
            SubtitleExportMode mode,
            CancellationToken cancellationToken)
        {
            Job = job;
            Mode = mode;
            return Task.FromResult("D:\\Output\\history.translated.srt");
        }
    }
}
