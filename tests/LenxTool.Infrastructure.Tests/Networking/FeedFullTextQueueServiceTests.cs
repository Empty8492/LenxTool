using System.Collections.Concurrent;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class FeedFullTextQueueServiceTests : IDisposable
{
    private const string CategoryId = "10000000-0000-4000-8000-000000000001";
    private const string FeedId = "20000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 3, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools full text queue tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackgroundBatchUsesLowGlobalAndPerHostConcurrency()
    {
        using SqliteDatabase database = await CreateDatabaseAsync(
        [
            Entry("one", "https://same.example/one"),
            Entry("two", "https://same.example/two"),
            Entry("three", "https://other.example/three")
        ]);
        var extractor = new TrackingExtractor();
        var repository = new FeedFullTextRepository(database);
        var service = new FeedFullTextQueueService(
            repository,
            extractor,
            Options(),
            new FrozenTimeProvider(Now));

        int firstAttempted = await service.ProcessBackgroundBatchAsync(CancellationToken.None);
        int secondAttempted = await service.ProcessBackgroundBatchAsync(CancellationToken.None);

        Assert.Equal(2, firstAttempted);
        Assert.Equal(1, secondAttempted);
        Assert.InRange(extractor.MaximumGlobalConcurrency, 1, 2);
        Assert.Equal(1, extractor.MaximumConcurrencyByHost["same.example"]);
        Assert.NotNull(await repository.GetContentAsync("one", CancellationToken.None));
        Assert.NotNull(await repository.GetContentAsync("two", CancellationToken.None));
        Assert.NotNull(await repository.GetContentAsync("three", CancellationToken.None));
        Assert.Equal(0, await service.ProcessBackgroundBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationReleasesClaimWithoutAddingFailureBackoff()
    {
        using SqliteDatabase database = await CreateDatabaseAsync(
        [
            Entry("cancelled", "https://cancel.example/article")
        ]);
        var extractor = new CancellingExtractor();
        var repository = new FeedFullTextRepository(database);
        var service = new FeedFullTextQueueService(
            repository,
            extractor,
            Options(),
            new FrozenTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();

        Task processing = service.ProcessBackgroundBatchAsync(cancellation.Token);
        await extractor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

        Assert.Single(await repository.ClaimBackgroundAsync(
            Now,
            10,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
    }

    [Fact]
    public async Task AccessDeniedIsBlockedAndDoesNotBypassTheSource()
    {
        using SqliteDatabase database = await CreateDatabaseAsync(
        [
            Entry("forbidden", "https://paywall.example/article")
        ]);
        var extractor = new ForbiddenExtractor();
        var repository = new FeedFullTextRepository(database);
        var service = new FeedFullTextQueueService(
            repository,
            extractor,
            Options(),
            new FrozenTimeProvider(Now));

        Assert.Equal(1, await service.ProcessBackgroundBatchAsync(CancellationToken.None));
        Assert.Equal(1, extractor.Attempts);
        Assert.Equal(0, await service.ProcessBackgroundBatchAsync(CancellationToken.None));
        Assert.Equal(1, extractor.Attempts);
        Assert.Null(await repository.GetContentAsync("forbidden", CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync(IReadOnlyList<FeedEntry> entries)
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        string feedUrl = "https://queue.example/feed.xml";
        await new FeedCatalogRepository(database).ReplaceAsync(new(
            new(1, FeedCatalogScope.Active, Now, Now),
            [new(CategoryId, "Technology", "technology", 1, true, 1, Now, Now)],
            [
                new(
                    FeedId,
                    feedUrl,
                    feedUrl,
                    "Queue",
                    null,
                    CategoryId,
                    FeedViewKind.Article,
                    60,
                    1,
                    true,
                    1,
                    Now,
                    Now,
                    FeedFullTextPolicy.Background)
            ]), CancellationToken.None);
        await new FeedEntryRepository(database).UpsertAsync(
            FeedId,
            entries,
            CancellationToken.None);
        return database;
    }

    private static FeedEntry Entry(string id, string url) => new(
        id,
        FeedId,
        id,
        url,
        id,
        null,
        Now,
        Now,
        "summary",
        "summary",
        [],
        [],
        new string('a', 64),
        Now,
        HasFullContent: false);

    private static FeedFullTextQueueOptions Options() => new(
        BatchSize: 10,
        MaximumConcurrency: 2,
        MaximumConcurrencyPerHost: 1,
        LeaseDuration: TimeSpan.FromMinutes(5),
        BaseRetryDelay: TimeSpan.FromMinutes(1),
        MaximumRetryDelay: TimeSpan.FromHours(1),
        InitialDelay: TimeSpan.Zero,
        PollInterval: TimeSpan.FromMinutes(5));

    private static ArticleContentResult Article(string url) => new(
        url,
        url,
        "Title",
        null,
        Now,
        [new(ArticleContentBlockKind.Paragraph, "Full article", null, null, [])],
        [],
        "test");

    private sealed class TrackingExtractor : IArticleContentExtractor
    {
        private readonly ConcurrentDictionary<string, int> _hostConcurrency =
            new(StringComparer.OrdinalIgnoreCase);
        private int _globalConcurrency;

        public int MaximumGlobalConcurrency { get; private set; }
        public ConcurrentDictionary<string, int> MaximumConcurrencyByHost { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public async Task<ArticleContentResult> ExtractAsync(
            string url,
            CancellationToken cancellationToken)
        {
            string host = new Uri(url).IdnHost;
            int global = Interlocked.Increment(ref _globalConcurrency);
            int hostCount = _hostConcurrency.AddOrUpdate(host, 1, static (_, current) => current + 1);
            MaximumGlobalConcurrency = Math.Max(MaximumGlobalConcurrency, global);
            MaximumConcurrencyByHost.AddOrUpdate(
                host,
                hostCount,
                (_, current) => Math.Max(current, hostCount));
            try
            {
                await Task.Delay(35, cancellationToken);
                return Article(url);
            }
            finally
            {
                Interlocked.Decrement(ref _globalConcurrency);
                _hostConcurrency.AddOrUpdate(host, 0, static (_, current) => current - 1);
            }
        }
    }

    private sealed class CancellingExtractor : IArticleContentExtractor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ArticleContentResult> ExtractAsync(
            string url,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Article(url);
        }
    }

    private sealed class ForbiddenExtractor : IArticleContentExtractor
    {
        public int Attempts { get; private set; }

        public Task<ArticleContentResult> ExtractAsync(
            string url,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new AppException(new(
                AppErrorCode.AccessDenied,
                "Forbidden",
                "The source refused access.",
                "Use the original website."));
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
