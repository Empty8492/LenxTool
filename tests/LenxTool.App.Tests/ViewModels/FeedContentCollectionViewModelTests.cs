using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class FeedContentCollectionViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PictureCollectionComposesFiltersAndUsesRepositoryContinuation()
    {
        FeedEntry picture = Entry(
            "picture",
            "https://cdn.example/picture.jpg",
            "image/jpeg");
        var entries = new StubEntryRepository();
        entries.Pages.Enqueue(
            new([picture], 0, HasMore: true, NextOffset: 17));
        entries.Pages.Enqueue(
            new([], 17, HasMore: false, NextOffset: 30));
        entries.Pages.Enqueue(
            new([], 0, HasMore: false, NextOffset: 30));
        var opened = new List<string>();
        using var viewModel = new FeedContentCollectionViewModel(
            EntryViewKind.Picture,
            "图片",
            entries,
            new StubCatalogRepository(),
            new StubEntryStateRepository(),
            new StubFavoriteRepository(),
            opened.Add);

        await viewModel.InitializeAsync(CancellationToken.None);

        FeedContentItem item = Assert.Single(viewModel.Items);
        Assert.Equal("https://cdn.example/picture.jpg", item.PrimaryImageUrl);
        Assert.Equal(EntryViewKind.Picture, Assert.Single(entries.Queries).ViewKind);
        Assert.Equal(17, viewModel.NextOffset);
        Assert.True(viewModel.HasMore);
        Assert.Equal(2, viewModel.Categories.Count);
        Assert.Equal(2, viewModel.Feeds.Count);

        viewModel.SelectedDate = new DateTime(2026, 7, 26);
        viewModel.FavoritesOnly = true;
        await viewModel.LoadMoreCommand.ExecuteAsync();

        FeedEntryQuery loadMore = entries.Queries[1];
        Assert.Equal(17, loadMore.Offset);
        Assert.Equal(EntryViewKind.Picture, loadMore.ViewKind);
        Assert.False(loadMore.FavoritesOnly);
        Assert.Null(loadMore.PublishedFrom);
        Assert.False(viewModel.HasMore);

        await viewModel.ApplyFiltersCommand.ExecuteAsync();

        FeedEntryQuery applied = entries.Queries[2];
        Assert.Equal(0, applied.Offset);
        Assert.True(applied.FavoritesOnly);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 25, 16, 0, 0, TimeSpan.Zero),
            applied.PublishedFrom);

        viewModel.OpenItemCommand.Execute(item);
        Assert.Equal("https://example.com/picture", Assert.Single(opened));
    }

    [Fact]
    public async Task UnsafeOriginalAndUnverifiedImageNeverBecomePictureActions()
    {
        FeedEntry unsafeEntry = Entry(
            "unsafe",
            "https://cdn.example/picture.jpg",
            "image/jpeg") with
        {
            NormalizedUrl = "https://user:password@example.com/article",
            Enclosures =
            [
                new(
                    "https://cdn.example/not-really-a-picture.jpg",
                    "text/html",
                    128,
                    "Unverified")
            ]
        };
        var entries = new StubEntryRepository();
        entries.Pages.Enqueue(
            new([unsafeEntry], 0, HasMore: false, NextOffset: 1));
        var opened = new List<string>();
        using var viewModel = new FeedContentCollectionViewModel(
            EntryViewKind.Picture,
            "图片",
            entries,
            new StubCatalogRepository(),
            new StubEntryStateRepository(),
            new StubFavoriteRepository(),
            opened.Add);

        await viewModel.InitializeAsync(CancellationToken.None);

        FeedContentItem item = Assert.Single(viewModel.Items);
        Assert.Null(item.PrimaryImageUrl);
        Assert.False(viewModel.OpenItemCommand.CanExecute(item));
        Assert.Empty(opened);
    }

    private static FeedEntry Entry(
        string id,
        string enclosureUrl,
        string mediaType) =>
        new(
            id,
            "30000000-0000-4000-8000-000000000001",
            id,
            $"https://example.com/{id}",
            id,
            "Author",
            Now,
            Now,
            "Summary",
            "Content",
            [],
            [new(enclosureUrl, mediaType, 128, id)],
            new string('a', 64),
            Now);

    private sealed class StubEntryRepository : IFeedEntryRepository
    {
        public Queue<FeedEntryPage> Pages { get; } = [];
        public List<FeedEntryQuery> Queries { get; } = [];

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(Pages.Dequeue());
        }

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedEntry?>(null);

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class StubCatalogRepository : IFeedCatalogRepository
    {
        private readonly FeedCatalogSnapshot _catalog = new(
            new(1, FeedCatalogScope.Active, Now, Now),
            [
                new(
                    "10000000-0000-4000-8000-000000000001",
                    "Technology",
                    "technology",
                    1,
                    true,
                    1,
                    Now,
                    Now)
            ],
            [
                new(
                    "30000000-0000-4000-8000-000000000001",
                    "https://feeds.example/feed.xml",
                    "https://feeds.example/feed.xml",
                    "Daily Feed",
                    "https://feeds.example/",
                    "10000000-0000-4000-8000-000000000001",
                    FeedViewKind.Article,
                    60,
                    1,
                    true,
                    1,
                    Now,
                    Now)
            ]);

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(_catalog);

        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(_catalog.State);

        public Task ReplaceAsync(
            FeedCatalogSnapshot snapshot,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubEntryStateRepository : IEntryStateRepository
    {
        public Task<IReadOnlyDictionary<string, EntryState>> GetAsync(
            IReadOnlyCollection<string> entryIds,
            string localProfile,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, EntryState>>(
                new Dictionary<string, EntryState>());

        public Task<EntryState> PatchAsync(
            string entryId,
            string localProfile,
            EntryStatePatch patch,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubFavoriteRepository : IFavoriteRepository
    {
        public Task<IReadOnlyDictionary<string, FavoriteItem>> GetForEntitiesAsync(
            string entityType,
            IReadOnlyCollection<string> entityIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, FavoriteItem>>(
                new Dictionary<string, FavoriteItem>());

        public Task<int> GetCountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);
        public Task<FavoriteItem?> GetAsync(string entityType, string entityId, CancellationToken cancellationToken) =>
            Task.FromResult<FavoriteItem?>(null);
        public Task<FavoriteItem> UpsertAsync(string entityType, string entityId, string note, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> RemoveAsync(string entityType, string entityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TagItem> UpsertTagAsync(string name, string color, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TagItem> AddTagAsync(string entityType, string entityId, string name, string color, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TagItem>> GetTagsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>([]);
        public Task<IReadOnlyList<TagItem>> GetTagsForEntityAsync(string entityType, string entityId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>([]);
        public Task SetTagsAsync(string entityType, string entityId, IReadOnlyCollection<string> tagIds, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<bool> DeleteTagAsync(string tagId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
