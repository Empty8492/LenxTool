using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationRuleSimulationServiceTests
{
    private const string FeedId = "10000000-0000-4000-8000-000000000001";
    private const string CategoryId = "10000000-0000-4000-8000-000000000002";

    [Fact]
    public async Task SimulationEvaluatesRecentEntriesWithoutWritingAnyState()
    {
        var entries = new FakeEntryRepository(
        [
            Entry("entry-1", "Release notes", "audio/mpeg"),
            Entry("entry-2", "Weekly digest", null)
        ]);
        var catalog = new FakeCatalogRepository(Catalog());
        var service = new FeedAutomationRuleSimulationService(entries, catalog);

        FeedAutomationSimulationResult result = await service.SimulateAsync(
            Definition(
                new(
                    FeedAutomationField.Title,
                    FeedAutomationOperator.Contains,
                    "release"),
                new(FeedAutomationActionType.Notify, 0, null)),
            20,
            CancellationToken.None);

        Assert.Equal(2, result.ExaminedCount);
        Assert.Equal(1, result.MatchedCount);
        Assert.Collection(
            result.Entries,
            item =>
            {
                Assert.Equal("entry-1", item.EntryId);
                Assert.Equal("示例源", item.SourceLabel);
                Assert.Equal(FeedAutomationRuleEvaluationOutcome.Matched, item.Outcome);
                Assert.Equal(
                    FeedAutomationActionType.Notify,
                    Assert.Single(item.Actions).Type);
            },
            item =>
            {
                Assert.Equal("entry-2", item.EntryId);
                Assert.Equal(FeedAutomationRuleEvaluationOutcome.NotMatched, item.Outcome);
                Assert.Empty(item.Actions);
            });
        Assert.Equal(0, entries.WriteCount);
        Assert.Equal(20, entries.LastQuery?.Limit);
        Assert.True(entries.LastQuery?.IncludeHidden);
    }

    [Fact]
    public async Task SimulationUsesCatalogCategoryAndMediaProjection()
    {
        var entries = new FakeEntryRepository(
            [Entry("entry-1", "Podcast", "audio/mpeg")]);
        var service = new FeedAutomationRuleSimulationService(
            entries,
            new FakeCatalogRepository(Catalog()));

        FeedAutomationSimulationResult category = await service.SimulateAsync(
            Definition(
                new(
                    FeedAutomationField.Category,
                    FeedAutomationOperator.Equals,
                    CategoryId),
                new(FeedAutomationActionType.AddTag, 0, "分类命中")),
            10,
            CancellationToken.None);
        FeedAutomationSimulationResult audio = await service.SimulateAsync(
            Definition(
                new(
                    FeedAutomationField.HasAudio,
                    FeedAutomationOperator.Equals,
                    "true"),
                new(FeedAutomationActionType.SendToMedia, 0, null)),
            10,
            CancellationToken.None);

        Assert.Equal(1, category.MatchedCount);
        Assert.Equal(1, audio.MatchedCount);
        Assert.Equal(
            FeedAutomationActionType.SendToMedia,
            Assert.Single(Assert.Single(audio.Entries).Actions).Type);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task InvalidSimulationLimitIsRejectedBeforeReading(int limit)
    {
        var entries = new FakeEntryRepository([]);
        var service = new FeedAutomationRuleSimulationService(
            entries,
            new FakeCatalogRepository(Catalog()));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.SimulateAsync(
                Definition(
                    new(
                        FeedAutomationField.Title,
                        FeedAutomationOperator.Exists,
                        null),
                    new(FeedAutomationActionType.Notify, 0, null)),
                limit,
                CancellationToken.None));

        Assert.Null(entries.LastQuery);
    }

    private static FeedAutomationRuleDefinition Definition(
        FeedAutomationCondition condition,
        FeedAutomationAction action) => new(
            "测试规则",
            100,
            10,
            true,
            FeedAutomationMatchMode.All,
            [condition],
            [action]);

    private static FeedEntry Entry(
        string id,
        string title,
        string? mediaType) => new(
            id,
            FeedId,
            $"external-{id}",
            $"https://example.com/{id}",
            title,
            "作者",
            new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero),
            null,
            "摘要",
            "正文",
            [],
            mediaType is null
                ? []
                : [new($"https://example.com/{id}.mp3", mediaType, 12, null)],
            $"hash-{id}",
            new DateTimeOffset(2026, 7, 26, 8, 5, 0, TimeSpan.Zero));

    private static FeedCatalogSnapshot Catalog()
    {
        DateTimeOffset now = new(2026, 7, 26, 8, 0, 0, TimeSpan.Zero);
        return new(
            new(1, FeedCatalogScope.All, now, now),
            [new(CategoryId, "技术", "技术", 0, true, 1, now, now)],
            [new(
                FeedId,
                "https://example.com/feed.xml",
                "https://example.com/feed.xml",
                "示例源",
                "https://example.com/",
                CategoryId,
                FeedViewKind.Article,
                60,
                0,
                true,
                1,
                now,
                now)]);
    }

    private sealed class FakeEntryRepository(IReadOnlyList<FeedEntry> items)
        : IFeedEntryRepository
    {
        public int WriteCount { get; private set; }
        public FeedEntryQuery? LastQuery { get; private set; }

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken) =>
            Task.FromResult(items.FirstOrDefault(item => item.Id == entryId));

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new FeedEntryPage(items, 0, false));
        }

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class FakeCatalogRepository(FeedCatalogSnapshot snapshot)
        : IFeedCatalogRepository
    {
        public Task ReplaceAsync(
            FeedCatalogSnapshot value,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulation must not write the catalog.");

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(snapshot);

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulation must not update synchronization state.");

        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshot.State);
    }
}
