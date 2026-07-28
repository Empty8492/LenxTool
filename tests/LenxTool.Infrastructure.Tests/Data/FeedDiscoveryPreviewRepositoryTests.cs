using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

/// <summary>
/// 用真实 SQLite 验证发现预览的一次批量查询、稳定排序、隐藏过滤和四条上限。
/// </summary>
public sealed class FeedDiscoveryPreviewRepositoryTests : IDisposable
{
    private const string FirstFeedId =
        "30000000-0000-4000-8000-000000000001";
    private const string SecondFeedId =
        "30000000-0000-4000-8000-000000000002";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools discovery preview tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BatchProjectionReturnsFourVisibleRecentItemsPerFeed()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var catalog = new FeedCatalogRepository(database);
        await catalog.ReplaceAsync(
            new(
                new(1, FeedCatalogScope.Active, Now, Now),
                [],
                [
                    Feed(FirstFeedId, "first"),
                    Feed(SecondFeedId, "second")
                ]),
            CancellationToken.None);
        var entries = new FeedEntryRepository(database);
        FeedEntry[] firstEntries = Enumerable.Range(1, 6)
            .Select(index => Entry(FirstFeedId, index))
            .ToArray();
        await entries.UpsertAsync(
            FirstFeedId,
            firstEntries,
            CancellationToken.None);
        await entries.UpsertAsync(
            SecondFeedId,
            [Entry(SecondFeedId, 1)],
            CancellationToken.None);

        // 最新条目被本地用户隐藏后，不应重新出现在管理员发现预览中。
        await using (SqliteConnection connection = await database
            .OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand hidden = connection.CreateCommand())
        {
            hidden.CommandText = """
                INSERT INTO user_entry_states(
                    entry_id,
                    local_profile,
                    is_read,
                    is_starred,
                    is_hidden,
                    progress,
                    note,
                    updated_at)
                VALUES($entryId, 'default', 0, 0, 1, 0, '', $updatedAt);
                """;
            hidden.Parameters.AddWithValue("$entryId", firstEntries[0].Id);
            hidden.Parameters.AddWithValue("$updatedAt", Now.ToString("O"));
            await hidden.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var repository = new FeedDiscoveryPreviewRepository(database);
        IReadOnlyList<FeedDiscoveryPreviewItem> result =
            await repository.GetRecentAsync(
                [FirstFeedId, SecondFeedId],
                4,
                "default",
                CancellationToken.None);

        FeedDiscoveryPreviewItem[] first = result
            .Where(item => item.FeedId == FirstFeedId)
            .ToArray();
        Assert.Equal(4, first.Length);
        Assert.Equal(
            ["第 2 条", "第 3 条", "第 4 条", "第 5 条"],
            first.Select(item => item.Title));
        Assert.DoesNotContain(
            result,
            item => item.Title == "第 1 条"
                && item.FeedId == FirstFeedId);
        Assert.Single(
            result,
            item => item.FeedId == SecondFeedId);
    }

    [Fact]
    public async Task MaximumCandidateWindowStaysWithinPreviewBudget()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        string[] feedIds = Enumerable.Range(1, 100)
            .Select(index =>
                $"31000000-0000-4000-8000-{index:D12}")
            .ToArray();
        var catalog = new FeedCatalogRepository(database);
        await catalog.ReplaceAsync(
            new(
                new(2, FeedCatalogScope.Active, Now, Now),
                [],
                feedIds.Select((id, index) =>
                    Feed(id, $"budget-{index:D3}")).ToArray()),
            CancellationToken.None);
        var entries = new FeedEntryRepository(database);
        foreach (string feedId in feedIds)
        {
            await entries.UpsertAsync(
                feedId,
                Enumerable.Range(1, 25)
                    .Select(index => Entry(feedId, index))
                    .ToArray(),
                CancellationToken.None);
        }

        var repository = new FeedDiscoveryPreviewRepository(database);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<FeedDiscoveryPreviewItem> result =
            await repository.GetRecentAsync(
                feedIds,
                4,
                "default",
                CancellationToken.None);
        stopwatch.Stop();
        Console.WriteLine(
            $"DISCOVERY_PREVIEW_ELAPSED_MS={stopwatch.Elapsed.TotalMilliseconds:F0}");

        Assert.Equal(400, result.Count);
        Assert.Equal(100, result.Select(item => item.FeedId).Distinct().Count());
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"发现预览最大候选窗口耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms，超过 2 秒预算。");
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        return database;
    }

    private static FeedCatalogItem Feed(string id, string slug) =>
        new(
            id,
            $"https://feeds.example/{slug}.xml",
            $"https://feeds.example/{slug}.xml",
            slug,
            $"https://feeds.example/{slug}/",
            null,
            FeedViewKind.Article,
            60,
            100,
            true,
            1,
            Now,
            Now);

    private static FeedEntry Entry(string feedId, int index) =>
        new(
            $"{feedId}-entry-{index}",
            feedId,
            $"external-{index}",
            $"https://feeds.example/items/{index}",
            $"第 {index} 条",
            "作者",
            Now.AddMinutes(-index),
            null,
            new string('摘', 256),
            new string('文', 8192),
            [],
            [],
            new string((char)('a' + index), 64),
            Now);

    public void Dispose()
    {
        // SQLite 连接池会继续持有测试文件，清池后才能可靠删除独立临时目录。
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
