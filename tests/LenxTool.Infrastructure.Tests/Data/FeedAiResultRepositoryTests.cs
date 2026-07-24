using System.Globalization;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedAiResultRepositoryTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools feed AI tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExactCacheKeyRoundTripsAndRepeatUpdatesTheSameHistoryRow()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedAiResultRepository(database);
        FeedAiCacheKey key = CreateKey(new string('a', 64));
        DateTimeOffset createdAt = DateTimeOffset.Parse(
            "2026-07-25T01:00:00Z",
            CultureInfo.InvariantCulture);

        await repository.UpsertAsync(
            CreateResult("feed-ai-1", key, "第一版摘要", createdAt),
            CancellationToken.None);
        await repository.UpsertAsync(
            CreateResult(
                "feed-ai-1",
                key,
                "第二版摘要",
                createdAt,
                updatedAt: createdAt.AddMinutes(2),
                requestCount: 2),
            CancellationToken.None);

        FeedAiResult? stored = await repository.GetCurrentAsync(
            key,
            CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("第二版摘要", stored.Content);
        Assert.Equal(2, stored.RequestCount);
        Assert.Single(await repository.GetHistoryAsync(
            key.EntryId,
            key.TaskType,
            key.TargetLanguage,
            limit: 20,
            CancellationToken.None));
    }

    [Fact]
    public async Task ChangedContentHashMissesOldCacheAndPreservesHistory()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedAiResultRepository(database);
        FeedAiCacheKey oldKey = CreateKey(new string('a', 64));
        FeedAiCacheKey newKey = oldKey with { ContentHash = new string('b', 64) };
        DateTimeOffset createdAt = DateTimeOffset.Parse(
            "2026-07-25T01:00:00Z",
            CultureInfo.InvariantCulture);

        await repository.UpsertAsync(
            CreateResult("feed-ai-old", oldKey, "旧正文摘要", createdAt),
            CancellationToken.None);

        Assert.Null(await repository.GetCurrentAsync(newKey, CancellationToken.None));

        await repository.UpsertAsync(
            CreateResult(
                "feed-ai-new",
                newKey,
                "新正文摘要",
                createdAt.AddMinutes(5)),
            CancellationToken.None);

        FeedAiResult? current = await repository.GetCurrentAsync(
            newKey,
            CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal("新正文摘要", current.Content);
        IReadOnlyList<FeedAiResult> history = await repository.GetHistoryAsync(
            newKey.EntryId,
            newKey.TaskType,
            newKey.TargetLanguage,
            limit: 20,
            CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Contains(history, item => item.CacheKey.ContentHash == oldKey.ContentHash);
        Assert.Contains(history, item => item.CacheKey.ContentHash == newKey.ContentHash);
    }

    [Fact]
    public async Task EveryCacheKeyDimensionParticipatesInLookup()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedAiResultRepository(database);
        FeedAiCacheKey key = CreateKey(new string('d', 64));
        DateTimeOffset createdAt = DateTimeOffset.Parse(
            "2026-07-25T01:00:00Z",
            CultureInfo.InvariantCulture);
        await repository.UpsertAsync(
            CreateResult("feed-ai-key", key, "精确缓存", createdAt),
            CancellationToken.None);

        FeedAiCacheKey[] misses =
        [
            key with { EntryId = "entry-2" },
            key with { ContentHash = new string('e', 64) },
            key with { TaskType = FeedAiTaskType.Translation },
            key with { TargetLanguage = "zh-CN" },
            key with { Model = "deepseek-v5" },
            key with { PromptVersion = "feed-summary-v2" }
        ];

        Assert.NotNull(await repository.GetCurrentAsync(key, CancellationToken.None));
        foreach (FeedAiCacheKey miss in misses)
        {
            Assert.Null(await repository.GetCurrentAsync(miss, CancellationToken.None));
        }
    }

    [Fact]
    public async Task UsageAndErrorTelemetryRoundTripsWithoutCredentialColumns()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedAiResultRepository(database);
        FeedAiCacheKey key = CreateKey(new string('c', 64));
        DateTimeOffset createdAt = DateTimeOffset.Parse(
            "2026-07-25T01:00:00Z",
            CultureInfo.InvariantCulture);
        FeedAiResult result = CreateResult(
            "feed-ai-error",
            key,
            string.Empty,
            createdAt,
            requestCount: 3,
            promptTokens: 120,
            completionTokens: 30,
            totalTokens: 150,
            durationMilliseconds: 1450,
            errorCode: "RATE_LIMITED");

        await repository.UpsertAsync(result, CancellationToken.None);

        FeedAiResult? stored = await repository.GetCurrentAsync(
            key,
            CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(3, stored.RequestCount);
        Assert.Equal(120, stored.PromptTokens);
        Assert.Equal(30, stored.CompletionTokens);
        Assert.Equal(150, stored.TotalTokens);
        Assert.Equal(1450, stored.DurationMilliseconds);
        Assert.Equal("RATE_LIMITED", stored.ErrorCode);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(ai_reports);";
        var columns = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            columns.Add(reader.GetString(1));
        }

        Assert.DoesNotContain(columns, column =>
            column.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || column.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || column.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private SqliteDatabase CreateDatabase() =>
        new(new AppPaths(_testRoot), NullLogger<SqliteDatabase>.Instance);

    private static FeedAiCacheKey CreateKey(string contentHash) =>
        new(
            "entry-1",
            contentHash,
            FeedAiTaskType.Summary,
            "und",
            "deepseek-v4-flash",
            "feed-summary-v1");

    private static FeedAiResult CreateResult(
        string id,
        FeedAiCacheKey key,
        string content,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt = null,
        int requestCount = 1,
        int promptTokens = 80,
        int completionTokens = 20,
        int totalTokens = 100,
        long durationMilliseconds = 900,
        string? errorCode = null) =>
        new(
            id,
            key,
            "条目摘要",
            content,
            requestCount,
            promptTokens,
            completionTokens,
            totalTokens,
            durationMilliseconds,
            errorCode,
            createdAt,
            updatedAt ?? createdAt);
}
