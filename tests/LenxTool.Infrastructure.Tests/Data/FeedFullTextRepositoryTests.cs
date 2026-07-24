using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedFullTextRepositoryTests : IDisposable
{
    private const string CategoryId = "10000000-0000-4000-8000-000000000001";
    private const string BackgroundFeedId = "20000000-0000-4000-8000-000000000001";
    private const string OnOpenFeedId = "20000000-0000-4000-8000-000000000002";
    private const string DisabledFeedId = "20000000-0000-4000-8000-000000000003";
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 3, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools full text repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackgroundClaimHonorsPolicyAndSkipsDisabledOrCompleteEntries()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var entries = new FeedEntryRepository(database);
        await entries.UpsertAsync(BackgroundFeedId,
        [
            Entry("background", BackgroundFeedId, "https://news.example/articles/background"),
            Entry("complete", BackgroundFeedId, "https://news.example/articles/complete", hasFullContent: true)
        ], CancellationToken.None);
        await entries.UpsertAsync(OnOpenFeedId,
        [
            Entry("on-open", OnOpenFeedId, "https://open.example/articles/one")
        ], CancellationToken.None);
        await entries.UpsertAsync(DisabledFeedId,
        [
            Entry("disabled", DisabledFeedId, "https://disabled.example/articles/one")
        ], CancellationToken.None);
        var repository = new FeedFullTextRepository(database);

        IReadOnlyList<FeedFullTextWorkItem> claimed = await repository.ClaimBackgroundAsync(
            Now,
            maximumCount: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None);

        FeedFullTextWorkItem item = Assert.Single(claimed);
        Assert.Equal("background", item.EntryId);
        Assert.Equal("news.example", item.Host);
        Assert.Null(await repository.ClaimOnOpenAsync(
            "complete",
            Now,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        Assert.NotNull(await repository.ClaimOnOpenAsync(
            "on-open",
            Now,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        Assert.Null(await repository.ClaimOnOpenAsync(
            "disabled",
            Now,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
    }

    [Fact]
    public async Task ClaimIsDeduplicatedAndHostFailureBacksOffOtherEntries()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var entries = new FeedEntryRepository(database);
        await entries.UpsertAsync(BackgroundFeedId,
        [
            Entry("first", BackgroundFeedId, "https://same.example/articles/first"),
            Entry("second", BackgroundFeedId, "https://same.example/articles/second")
        ], CancellationToken.None);
        var repository = new FeedFullTextRepository(database);

        FeedFullTextWorkItem first = Assert.Single(await repository.ClaimBackgroundAsync(
            Now,
            maximumCount: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None));
        Assert.Empty(await repository.ClaimBackgroundAsync(
            Now,
            maximumCount: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None));

        await repository.ScheduleRetryAsync(
            first,
            "NETWORK_UNAVAILABLE",
            Now.AddMinutes(10),
            Now,
            CancellationToken.None);

        Assert.Empty(await repository.ClaimBackgroundAsync(
            Now.AddMinutes(1),
            maximumCount: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None));
        Assert.NotEmpty(await repository.ClaimBackgroundAsync(
            Now.AddMinutes(11),
            maximumCount: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None));
    }

    [Fact]
    public async Task PolicySwitchStopsNewBackgroundClaimsAndEnablesOnOpenClaims()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var entries = new FeedEntryRepository(database);
        await entries.UpsertAsync(BackgroundFeedId,
        [
            Entry("switchable", BackgroundFeedId, "https://switch.example/articles/one")
        ], CancellationToken.None);
        var repository = new FeedFullTextRepository(database);
        FeedFullTextWorkItem initial = Assert.Single(await repository.ClaimBackgroundAsync(
            Now,
            maximumCount: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None));
        await repository.ReleaseAsync(initial, Now, CancellationToken.None);

        await ReplaceSingleFeedAsync(
            database,
            version: 2,
            FeedFullTextPolicy.None,
            isEnabled: true);
        Assert.Empty(await repository.ClaimBackgroundAsync(
            Now,
            10,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        Assert.Null(await repository.ClaimOnOpenAsync(
            "switchable",
            Now,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));

        await ReplaceSingleFeedAsync(
            database,
            version: 3,
            FeedFullTextPolicy.OnOpen,
            isEnabled: true);
        Assert.Empty(await repository.ClaimBackgroundAsync(
            Now,
            10,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        Assert.NotNull(await repository.ClaimOnOpenAsync(
            "switchable",
            Now,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
    }

    [Fact]
    public async Task SuccessfulContentPersistsAndIsNeverClaimedAgain()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var entries = new FeedEntryRepository(database);
        await entries.UpsertAsync(BackgroundFeedId,
        [
            Entry("success", BackgroundFeedId, "https://content.example/articles/success")
        ], CancellationToken.None);
        var repository = new FeedFullTextRepository(database);
        FeedFullTextWorkItem work = Assert.Single(await repository.ClaimBackgroundAsync(
            Now,
            maximumCount: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None));
        ArticleContentResult article = Article(work.Url);

        await repository.SaveContentAsync(work, article, Now, CancellationToken.None);

        FeedFullTextContent stored = Assert.IsType<FeedFullTextContent>(
            await repository.GetContentAsync(work.EntryId, CancellationToken.None));
        Assert.Equal(article.RequestedUrl, stored.Article.RequestedUrl);
        Assert.Equal(article.FinalUrl, stored.Article.FinalUrl);
        Assert.Equal(article.Title, stored.Article.Title);
        Assert.Equal(article.Author, stored.Article.Author);
        Assert.Equal(article.PublishedAt, stored.Article.PublishedAt);
        ArticleContentBlock expectedBlock = Assert.Single(article.Blocks);
        ArticleContentBlock storedBlock = Assert.Single(stored.Article.Blocks);
        Assert.Equal(expectedBlock.Kind, storedBlock.Kind);
        Assert.Equal(expectedBlock.Text, storedBlock.Text);
        Assert.Equal(expectedBlock.ResourceUrl, storedBlock.ResourceUrl);
        Assert.Equal(expectedBlock.HeadingLevel, storedBlock.HeadingLevel);
        Assert.Empty(storedBlock.Links);
        Assert.Empty(stored.Article.Warnings);
        Assert.Equal(article.ExtractorVersion, stored.Article.ExtractorVersion);
        Assert.Equal(64, stored.ContentHash.Length);
        Assert.Equal(Now, stored.ExtractedAt);
        Assert.Empty(await repository.ClaimBackgroundAsync(
            Now.AddDays(1),
            maximumCount: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredLeaseCannotOverwriteTheNewOwner()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var entries = new FeedEntryRepository(database);
        await entries.UpsertAsync(BackgroundFeedId,
        [
            Entry("leased", BackgroundFeedId, "https://lease.example/articles/one")
        ], CancellationToken.None);
        var repository = new FeedFullTextRepository(database);
        FeedFullTextWorkItem expired = Assert.Single(await repository.ClaimBackgroundAsync(
            Now,
            maximumCount: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None));
        FeedFullTextWorkItem current = Assert.Single(await repository.ClaimBackgroundAsync(
            Now.AddMinutes(6),
            maximumCount: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveContentAsync(
            expired,
            Article(expired.Url),
            Now.AddMinutes(6),
            CancellationToken.None));
        await repository.SaveContentAsync(
            current,
            Article(current.Url),
            Now.AddMinutes(7),
            CancellationToken.None);

        Assert.NotNull(await repository.GetContentAsync("leased", CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        await new FeedCatalogRepository(database).ReplaceAsync(new(
            new(1, FeedCatalogScope.All, Now, Now),
            [
                new(CategoryId, "Technology", "technology", 1, true, 1, Now, Now)
            ],
            [
                Feed(BackgroundFeedId, "background", FeedFullTextPolicy.Background, isEnabled: true),
                Feed(OnOpenFeedId, "on-open", FeedFullTextPolicy.OnOpen, isEnabled: true),
                Feed(DisabledFeedId, "disabled", FeedFullTextPolicy.Background, isEnabled: false)
            ]), CancellationToken.None);
        return database;
    }

    private static Task ReplaceSingleFeedAsync(
        SqliteDatabase database,
        long version,
        FeedFullTextPolicy policy,
        bool isEnabled) => new FeedCatalogRepository(database).ReplaceAsync(new(
        new(version, FeedCatalogScope.All, Now, Now),
        [new(CategoryId, "Technology", "technology", 1, true, version, Now, Now)],
        [Feed(BackgroundFeedId, "background", policy, isEnabled)]),
        CancellationToken.None);

    private static FeedCatalogItem Feed(
        string id,
        string suffix,
        FeedFullTextPolicy policy,
        bool isEnabled)
    {
        string url = $"https://{suffix}.example/feed.xml";
        return new(
            id,
            url,
            url,
            suffix,
            null,
            CategoryId,
            FeedViewKind.Article,
            60,
            1,
            isEnabled,
            1,
            Now,
            Now,
            policy);
    }

    private static FeedEntry Entry(
        string id,
        string feedId,
        string url,
        bool hasFullContent = false) => new(
        id,
        feedId,
        id,
        url,
        id,
        null,
        Now,
        Now,
        "summary",
        hasFullContent ? "complete article body" : "summary",
        [],
        [],
        new string('a', 64),
        Now,
        hasFullContent);

    private static ArticleContentResult Article(string url) => new(
        url,
        url,
        "Extracted title",
        "Author",
        Now,
        [
            new(
                ArticleContentBlockKind.Paragraph,
                "Extracted article body",
                null,
                null,
                [])
        ],
        [],
        "test-extractor");
}
