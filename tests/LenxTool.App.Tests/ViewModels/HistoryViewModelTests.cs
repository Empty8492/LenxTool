using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using System.Globalization;

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
            new StubNewsRepository([expected]));
        viewModel.SearchQuery = "人工智能";

        await viewModel.SearchCommand.ExecuteAsync();

        Assert.Same(expected, Assert.Single(viewModel.SearchResults));
        Assert.Equal("找到 1 条相关内容。", viewModel.SearchStatus);
        Assert.Same(expected, viewModel.SelectedSearchResult);
    }

    [Fact]
    public void SearchCommandRequiresNonWhitespaceQuery()
    {
        var viewModel = new HistoryViewModel(
            new StubMediaJobRepository(),
            new StubDatabaseMaintenanceService(),
            new StubDialogs(),
            new StubNewsRepository([]));

        viewModel.SearchQuery = "   ";

        Assert.False(viewModel.SearchCommand.CanExecute(null));
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

    private sealed class StubMediaJobRepository : IMediaJobRepository
    {
        public Task UpsertAsync(MediaJob job, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MediaJob>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaJob>>([]);
        public Task<IReadOnlyList<MediaJob>> GetQueuedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaJob>>([]);
        public Task<IReadOnlyList<MediaJob>> RecoverInterruptedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaJob>>([]);
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
}
