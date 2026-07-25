using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationLocalActionServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAppliesOnlyBoundedLocalActions()
    {
        FeedEntry entry = Entry();
        var entries = new StubEntryRepository(entry);
        var states = new StubEntryStateRepository();
        var favorites = new StubFavoriteRepository();
        var service = new FeedAutomationLocalActionService(
            entries,
            states,
            favorites);

        Assert.Equal(
            FeedAutomationLocalActionResult.Completed,
            await service.ExecuteAsync(
                Lease(FeedAutomationActionType.MarkRead),
                CancellationToken.None));
        Assert.Equal(
            FeedAutomationLocalActionResult.Completed,
            await service.ExecuteAsync(
                Lease(FeedAutomationActionType.Hide),
                CancellationToken.None));
        Assert.Equal(
            FeedAutomationLocalActionResult.Completed,
            await service.ExecuteAsync(
                Lease(FeedAutomationActionType.AddTag, "AI"),
                CancellationToken.None));

        Assert.Contains(states.Patches, patch => patch.IsRead == true);
        Assert.Contains(states.Patches, patch => patch.IsHidden == true);
        Assert.Equal(
            ("feed_entry", entry.Id, "AI", "#4B6B88"),
            Assert.Single(favorites.AddedTags));
    }

    [Fact]
    public async Task MissingEntryReturnsTerminalResultWithoutMutatingPrivateState()
    {
        var entries = new StubEntryRepository(entry: null);
        var states = new StubEntryStateRepository();
        var favorites = new StubFavoriteRepository();
        var service = new FeedAutomationLocalActionService(
            entries,
            states,
            favorites);

        FeedAutomationLocalActionResult result = await service.ExecuteAsync(
            Lease(FeedAutomationActionType.MarkRead),
            CancellationToken.None);

        Assert.Equal(FeedAutomationLocalActionResult.EntryMissing, result);
        Assert.Empty(states.Patches);
        Assert.Empty(favorites.AddedTags);
    }

    [Theory]
    [InlineData(FeedAutomationActionType.GenerateSummary)]
    [InlineData(FeedAutomationActionType.Translate)]
    [InlineData(FeedAutomationActionType.SendToMedia)]
    [InlineData(FeedAutomationActionType.Notify)]
    public async Task ExecuteRejectsNonLocalActionsBeforeReadingEntry(
        FeedAutomationActionType type)
    {
        var entries = new StubEntryRepository(Entry());
        var service = new FeedAutomationLocalActionService(
            entries,
            new StubEntryStateRepository(),
            new StubFavoriteRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            Lease(type, type == FeedAutomationActionType.Translate ? "en" : null),
            CancellationToken.None));

        Assert.Equal(0, entries.GetCalls);
    }

    [Fact]
    public async Task ExecuteRejectsInvalidLocalPayloadBeforeMutation()
    {
        var entries = new StubEntryRepository(Entry());
        var states = new StubEntryStateRepository();
        var favorites = new StubFavoriteRepository();
        var service = new FeedAutomationLocalActionService(
            entries,
            states,
            favorites);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExecuteAsync(
            Lease(FeedAutomationActionType.AddTag),
            CancellationToken.None));

        Assert.Equal(0, entries.GetCalls);
        Assert.Empty(states.Patches);
        Assert.Empty(favorites.AddedTags);
    }

    [Fact]
    public async Task ExecuteRejectsOversizedEntryIdentityBeforeReadingEntry()
    {
        var entries = new StubEntryRepository(Entry());
        var service = new FeedAutomationLocalActionService(
            entries,
            new StubEntryStateRepository(),
            new StubFavoriteRepository());
        FeedAutomationActionLease action = Lease(FeedAutomationActionType.Hide) with
        {
            EntryId = new string('e', 129)
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExecuteAsync(
            action,
            CancellationToken.None));

        Assert.Equal(0, entries.GetCalls);
    }

    private static FeedAutomationActionLease Lease(
        FeedAutomationActionType type,
        string? value = null) =>
        new(
            new string('a', 64),
            Entry().Id,
            "30000000-0000-4000-8000-000000000093",
            1,
            100,
            0,
            type,
            10,
            value,
            1,
            new string('b', 32));

    private static FeedEntry Entry() =>
        new(
            "entry-local-action",
            "20000000-0000-4000-8000-000000000093",
            "entry-local-action",
            "https://news.example/local-action",
            "Local action",
            null,
            Now,
            Now,
            "Summary",
            "<p>Content</p>",
            [],
            [],
            new string('c', 64),
            Now);

    private sealed class StubEntryRepository(FeedEntry? entry) : IFeedEntryRepository
    {
        public int GetCalls { get; private set; }

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult(
                string.Equals(entry?.Id, entryId, StringComparison.Ordinal)
                    ? entry
                    : null);
        }

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FeedEntryPage([], query.Offset, false));

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubEntryStateRepository : IEntryStateRepository
    {
        public List<EntryStatePatch> Patches { get; } = [];

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
            CancellationToken cancellationToken)
        {
            Patches.Add(patch);
            return Task.FromResult(new EntryState(
                entryId,
                localProfile,
                patch.IsRead ?? false,
                patch.IsStarred ?? false,
                patch.IsHidden ?? false,
                patch.Progress ?? 0,
                patch.Note ?? string.Empty,
                Now));
        }
    }

    private sealed class StubFavoriteRepository : IFavoriteRepository
    {
        public List<(string EntityType, string EntityId, string Name, string Color)>
            AddedTags { get; } = [];

        public Task<TagItem> AddTagAsync(
            string entityType,
            string entityId,
            string name,
            string color,
            CancellationToken cancellationToken)
        {
            AddedTags.Add((entityType, entityId, name, color));
            return Task.FromResult(new TagItem("tag-1", name, color, Now));
        }

        public Task<int> GetCountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<FavoriteItem?> GetAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult<FavoriteItem?>(null);

        public Task<FavoriteItem> UpsertAsync(
            string entityType,
            string entityId,
            string note,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IReadOnlyDictionary<string, FavoriteItem>> GetForEntitiesAsync(
            string entityType,
            IReadOnlyCollection<string> entityIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, FavoriteItem>>(
                new Dictionary<string, FavoriteItem>());

        public Task<TagItem> UpsertTagAsync(
            string name,
            string color,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TagItem>> GetTagsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>([]);

        public Task<IReadOnlyList<TagItem>> GetTagsForEntityAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagItem>>([]);

        public Task SetTagsAsync(
            string entityType,
            string entityId,
            IReadOnlyCollection<string> tagIds,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> DeleteTagAsync(
            string tagId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
