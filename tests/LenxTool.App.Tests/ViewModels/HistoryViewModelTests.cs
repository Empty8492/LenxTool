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
            new StubSubtitleExportService(),
            new StubHistoryEntryStateRepository(),
            new StubHistoryFavoriteRepository());
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
            new StubSubtitleExportService(),
            new StubHistoryEntryStateRepository(),
            new StubHistoryFavoriteRepository())
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
            new StubSubtitleExportService(),
            new StubHistoryEntryStateRepository(),
            new StubHistoryFavoriteRepository());

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
            exporter,
            new StubHistoryEntryStateRepository(),
            new StubHistoryFavoriteRepository());

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

    [Fact]
    public async Task FeedSearchResultProvidesConsistentPrivateStateEditor()
    {
        var result = new ContentSearchResult(
            "feed-entry-1",
            ContentSearchResultType.FeedEntry,
            "Feed 搜索结果",
            "本机私人状态",
            "Daily Feed",
            "https://feeds.example/entry/1",
            DateTimeOffset.UtcNow);
        var states = new StubHistoryEntryStateRepository();
        var favorites = new StubHistoryFavoriteRepository();
        TagItem originalTag = favorites.SeedTag("精读");
        favorites.SeedFavorite(result.EntityId, "已保存备注", originalTag);
        var viewModel = new HistoryViewModel(
            new StubMediaJobRepository(),
            new StubDatabaseMaintenanceService(),
            new StubDialogs(),
            new StubNewsRepository([result]),
            new StubMediaJobRepository(),
            new StubSubtitleExportService(),
            states,
            favorites)
        {
            SearchQuery = "Feed"
        };

        await viewModel.SearchCommand.ExecuteAsync();
        await viewModel.SelectedSearchPrivateStateLoad;

        Assert.True(viewModel.SelectedSearchIsFeedEntry);
        Assert.True(viewModel.SelectedSearchIsStarred);
        Assert.False(viewModel.SelectedSearchIsRead);
        Assert.Equal("已保存备注", viewModel.SelectedSearchPrivateNote);
        Assert.Equal(originalTag, Assert.Single(viewModel.SelectedSearchTags));

        await viewModel.ToggleSelectedSearchReadCommand.ExecuteAsync(result);
        Assert.True(viewModel.SelectedSearchIsRead);
        Assert.True(states.States[result.EntityId].IsRead);

        viewModel.SelectedSearchPrivateNote = "历史页更新备注";
        await viewModel.SaveSelectedSearchNoteCommand.ExecuteAsync();
        Assert.Equal("历史页更新备注", favorites.GetFavorite(result.EntityId)?.Note);
        Assert.Equal("历史页更新备注", states.States[result.EntityId].Note);

        viewModel.SelectedSearchPrivateNote = "尚未保存";
        viewModel.CancelSelectedSearchNoteCommand.Execute(null);
        Assert.Equal("历史页更新备注", viewModel.SelectedSearchPrivateNote);

        viewModel.SelectedSearchTagInput = "稍后阅读";
        await viewModel.AddSelectedSearchTagCommand.ExecuteAsync();
        TagItem added = Assert.Single(viewModel.SelectedSearchTags, tag => tag.Name == "稍后阅读");
        await viewModel.RemoveSelectedSearchTagCommand.ExecuteAsync(added);
        Assert.DoesNotContain(viewModel.SelectedSearchTags, tag => tag.Id == added.Id);

        await viewModel.ToggleSelectedSearchStarCommand.ExecuteAsync(result);
        Assert.False(viewModel.SelectedSearchIsStarred);
        Assert.Null(favorites.GetFavorite(result.EntityId));
        Assert.False(states.States[result.EntityId].IsStarred);
        Assert.Equal("历史页更新备注", states.States[result.EntityId].Note);
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

    private sealed class StubHistoryEntryStateRepository : IEntryStateRepository
    {
        public Dictionary<string, EntryState> States { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, EntryState>> GetAsync(
            IReadOnlyCollection<string> entryIds,
            string localProfile,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, EntryState> result = entryIds
                .Where(States.ContainsKey)
                .ToDictionary(id => id, id => States[id], StringComparer.Ordinal);
            return Task.FromResult(result);
        }

        public Task<EntryState> PatchAsync(
            string entryId,
            string localProfile,
            EntryStatePatch patch,
            CancellationToken cancellationToken)
        {
            EntryState current = States.GetValueOrDefault(entryId)
                ?? new(
                    entryId,
                    localProfile,
                    false,
                    false,
                    false,
                    0,
                    string.Empty,
                    DateTimeOffset.UtcNow);
            EntryState updated = current with
            {
                IsRead = patch.IsRead ?? current.IsRead,
                IsStarred = patch.IsStarred ?? current.IsStarred,
                IsHidden = patch.IsHidden ?? current.IsHidden,
                Progress = patch.Progress ?? current.Progress,
                Note = patch.Note ?? current.Note,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            States[entryId] = updated;
            return Task.FromResult(updated);
        }
    }

    private sealed class StubHistoryFavoriteRepository : IFavoriteRepository
    {
        private const string EntityType = "feed_entry";
        private readonly Dictionary<string, FavoriteItem> _favorites = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TagItem> _tags = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _entityTags = new(StringComparer.Ordinal);

        public FavoriteItem? GetFavorite(string entityId) => _favorites.GetValueOrDefault(entityId);

        public TagItem SeedTag(string name)
        {
            var tag = new TagItem($"tag-{_tags.Count + 1}", name, "#4B6B88", DateTimeOffset.UtcNow);
            _tags[tag.Id] = tag;
            return tag;
        }

        public void SeedFavorite(string entityId, string note, params TagItem[] tags)
        {
            _favorites[entityId] = new(
                $"favorite-{entityId}",
                EntityType,
                entityId,
                note,
                DateTimeOffset.UtcNow);
            _entityTags[entityId] = tags.Select(tag => tag.Id).ToHashSet(StringComparer.Ordinal);
        }

        public Task<int> GetCountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_favorites.Count);

        public Task<FavoriteItem?> GetAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_favorites.GetValueOrDefault(entityId));

        public Task<FavoriteItem> UpsertAsync(
            string entityType,
            string entityId,
            string note,
            CancellationToken cancellationToken)
        {
            FavoriteItem favorite = _favorites.GetValueOrDefault(entityId) is { } current
                ? current with { Note = note }
                : new($"favorite-{entityId}", entityType, entityId, note, DateTimeOffset.UtcNow);
            _favorites[entityId] = favorite;
            return Task.FromResult(favorite);
        }

        public Task<bool> RemoveAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_favorites.Remove(entityId));

        public Task<IReadOnlyDictionary<string, FavoriteItem>> GetForEntitiesAsync(
            string entityType,
            IReadOnlyCollection<string> entityIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, FavoriteItem>>(
                entityIds
                    .Where(_favorites.ContainsKey)
                    .ToDictionary(id => id, id => _favorites[id], StringComparer.Ordinal));

        public Task<TagItem> UpsertTagAsync(
            string name,
            string color,
            CancellationToken cancellationToken)
        {
            string normalized = name.Trim();
            TagItem tag = _tags.Values.FirstOrDefault(
                item => string.Equals(item.Name, normalized, StringComparison.OrdinalIgnoreCase))
                ?? SeedTag(normalized);
            return Task.FromResult(tag);
        }

        public async Task<TagItem> AddTagAsync(
            string entityType,
            string entityId,
            string name,
            string color,
            CancellationToken cancellationToken)
        {
            TagItem tag = await UpsertTagAsync(name, color, cancellationToken);
            if (!_entityTags.TryGetValue(entityId, out HashSet<string>? tagIds))
            {
                tagIds = new(StringComparer.Ordinal);
                _entityTags[entityId] = tagIds;
            }
            tagIds.Add(tag.Id);
            return tag;
        }

        public Task<IReadOnlyList<TagItem>> GetTagsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>(_tags.Values.ToArray());

        public Task<IReadOnlyList<TagItem>> GetTagsForEntityAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<TagItem> tags = _entityTags.GetValueOrDefault(entityId)?
                .Select(id => _tags[id])
                .ToArray()
                ?? [];
            return Task.FromResult(tags);
        }

        public Task SetTagsAsync(
            string entityType,
            string entityId,
            IReadOnlyCollection<string> tagIds,
            CancellationToken cancellationToken)
        {
            _entityTags[entityId] = tagIds.ToHashSet(StringComparer.Ordinal);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteTagAsync(string tagId, CancellationToken cancellationToken) =>
            Task.FromResult(_tags.Remove(tagId));
    }
}
