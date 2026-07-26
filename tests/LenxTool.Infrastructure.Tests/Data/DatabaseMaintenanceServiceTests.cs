using System.Text;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class DatabaseMaintenanceServiceTests : IDisposable
{
    private const string CategoryId =
        "10000000-0000-4000-8000-000000000001";
    private const string FeedId =
        "30000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools database maintenance tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreviewAndCleanupRespectRetentionBoundaryAndPrivateReferences()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = new(_testRoot);
        var entries = new FeedEntryRepository(database);
        var assets = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 1024, MaximumAssetBytes: 128));
        DateTimeOffset cutoff = Now.AddDays(-180);
        FeedEntry expired = Entry(
            "expired",
            "Expired",
            cutoff.AddSeconds(-1));
        FeedEntry boundary = Entry(
            "boundary",
            "Boundary",
            cutoff);
        FeedEntry protectedEntry = Entry(
            "protected",
            "Protected",
            cutoff.AddDays(-20));
        FeedEntry recent = Entry(
            "recent",
            "Recent",
            Now.AddDays(-2));
        await entries.UpsertAsync(
            FeedId,
            [expired, boundary, protectedEntry, recent],
            CancellationToken.None);
        await using (SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand favorite = connection.CreateCommand())
        {
            favorite.CommandText = """
                INSERT INTO favorites(
                    id, entity_type, entity_id, note, created_at)
                VALUES(
                    'favorite-protected', 'feed_entry', $entryId,
                    '保留私人备注', $now);
                """;
            favorite.Parameters.AddWithValue("$entryId", protectedEntry.Id);
            favorite.Parameters.AddWithValue("$now", Now.ToString("O"));
            await favorite.ExecuteNonQueryAsync(CancellationToken.None);
        }
        EntryAsset expiredAsset = await assets.PutAsync(
            expired.Id,
            "https://cdn.example.test/expired.png",
            "image/png",
            new MemoryStream([1, 2, 3, 4]),
            CancellationToken.None);
        EntryAsset protectedAsset = await assets.PutAsync(
            protectedEntry.Id,
            "https://cdn.example.test/protected.png",
            "image/png",
            new MemoryStream([5, 6, 7]),
            CancellationToken.None);
        await File.WriteAllBytesAsync(
            Path.Combine(paths.ModelsDirectory, "ggml-test.bin"),
            [8, 9, 10],
            CancellationToken.None);
        var service = new DatabaseMaintenanceService(
            paths,
            database,
            entries,
            assets);

        LocalStorageUsage usage =
            await service.GetStorageUsageAsync(CancellationToken.None);
        StorageCleanupPreview preview =
            await service.PreviewCleanupAsync(
                cutoff,
                CancellationToken.None);

        Assert.True(usage.DatabaseBytes > 0);
        Assert.Equal(7, usage.ImageCacheBytes);
        Assert.Equal(2, usage.ImageFileCount);
        Assert.Equal(3, usage.ModelBytes);
        Assert.Equal(1, usage.ModelFileCount);
        Assert.Equal(1, preview.ExpiredFeedEntryCount);
        Assert.Equal(1, preview.ReclaimableImageFileCount);
        Assert.Equal(4, preview.ReclaimableImageBytes);

        StorageCleanupResult result = await service.RunCleanupAsync(
            cutoff,
            CancellationToken.None);

        Assert.Equal(1, result.DeletedFeedEntryCount);
        Assert.Equal(1, result.RemovedImageFileCount);
        Assert.Equal(4, result.ReclaimedImageBytes);
        Assert.DoesNotContain(
            (await entries.QueryAsync(Query(), CancellationToken.None)).Items,
            item => item.Id == expired.Id);
        Assert.Contains(
            (await entries.QueryAsync(Query(), CancellationToken.None)).Items,
            item => item.Id == boundary.Id);
        Assert.Contains(
            (await entries.QueryAsync(Query(), CancellationToken.None)).Items,
            item => item.Id == protectedEntry.Id);
        Assert.Null(await assets.GetAsync(
            expired.Id,
            expiredAsset.SourceUrl,
            CancellationToken.None));
        Assert.NotNull(await assets.GetAsync(
            protectedEntry.Id,
            protectedAsset.SourceUrl,
            CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(
            paths.AssetCacheDirectory,
            expiredAsset.ContentHash)));
        Assert.True(File.Exists(Path.Combine(
            paths.AssetCacheDirectory,
            protectedAsset.ContentHash)));
    }

    [Fact]
    public async Task PreCanceledCleanupDoesNotDeleteCandidates()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = new(_testRoot);
        var entries = new FeedEntryRepository(database);
        var assets = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 1024, MaximumAssetBytes: 128));
        FeedEntry expired = Entry(
            "cancelled",
            "Cancelled",
            Now.AddDays(-181));
        await entries.UpsertAsync(
            FeedId,
            [expired],
            CancellationToken.None);
        var service = new DatabaseMaintenanceService(
            paths,
            database,
            entries,
            assets);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RunCleanupAsync(
                Now.AddDays(-180),
                cancellation.Token));

        Assert.NotNull(await entries.GetByIdAsync(
            expired.Id,
            CancellationToken.None));
    }

    [Fact]
    public async Task CleanupSkipsVacuumWhenDiskHasNoSafeWorkingSpace()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = new(_testRoot);
        var entries = new FeedEntryRepository(database);
        using var assets = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 1024, MaximumAssetBytes: 128));
        FeedEntry expired = Entry(
            "disk-full",
            "Disk Full",
            Now.AddDays(-181));
        await entries.UpsertAsync(
            FeedId,
            [expired],
            CancellationToken.None);
        var service = new DatabaseMaintenanceService(
            paths,
            database,
            entries,
            assets)
        {
            VacuumCapacityProbe = () => false
        };

        StorageCleanupResult result = await service.RunCleanupAsync(
            Now.AddDays(-180),
            CancellationToken.None);

        Assert.Equal(1, result.DeletedFeedEntryCount);
        Assert.False(result.DatabaseOptimized);
        Assert.Null(await entries.GetByIdAsync(
            expired.Id,
            CancellationToken.None));
    }

    [Fact]
    public async Task CleanupProcessesLargeLibrariesAcrossBoundedBatches()
    {
        const int entryCount = 10_001;
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = new(_testRoot);
        var entries = new FeedEntryRepository(database);
        using var assets = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 1024, MaximumAssetBytes: 128));
        await InsertExpiredEntriesAsync(database, entryCount);
        var service = new DatabaseMaintenanceService(
            paths,
            database,
            entries,
            assets)
        {
            VacuumCapacityProbe = () => false
        };

        StorageCleanupResult result = await service.RunCleanupAsync(
            Now.AddDays(-180),
            CancellationToken.None);

        Assert.Equal(entryCount, result.DeletedFeedEntryCount);
        Assert.Equal(0, await CountFeedEntriesAsync(database));
        Assert.False(result.DatabaseOptimized);
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var paths = new AppPaths(_testRoot);
        var database = new SqliteDatabase(
            paths,
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        await new FeedCatalogRepository(database).ReplaceAsync(
            new(
                new(1, FeedCatalogScope.Active, Now.AddHours(-1), Now),
                [
                    new(
                        CategoryId,
                        "Technology",
                        "technology",
                        1,
                        true,
                        1,
                        Now.AddDays(-1),
                        Now)
                ],
                [
                    new(
                        FeedId,
                        "https://feeds.example/daily.xml",
                        "https://feeds.example/daily.xml",
                        "Daily Feed",
                        "https://feeds.example/",
                        CategoryId,
                        FeedViewKind.Article,
                        60,
                        1,
                        true,
                        1,
                        Now.AddDays(-1),
                        Now)
                ]),
            CancellationToken.None);
        return database;
    }

    private static FeedEntry Entry(
        string externalId,
        string title,
        DateTimeOffset publishedAt)
    {
        string xml =
            $"<rss version='2.0'><channel><title>x</title>" +
            $"<item><guid>{externalId}</guid><title>{title}</title>" +
            $"<pubDate>{publishedAt:R}</pubDate>" +
            "<description>retention content</description></item>" +
            "</channel></rss>";
        return Assert.Single(new FeedDocumentParser().Parse(
            FeedId,
            "https://feeds.example/daily.xml",
            Encoding.UTF8.GetBytes(xml),
            Now).Entries);
    }

    private static FeedEntryQuery Query() => new(
        null,
        null,
        null,
        null,
        null,
        FeedEntryReadFilter.All,
        0,
        20);

    private static async Task InsertExpiredEntriesAsync(
        SqliteDatabase database,
        int count)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE sequence(value) AS (
                VALUES(1)
                UNION ALL
                SELECT value + 1
                FROM sequence
                WHERE value < $count
            )
            INSERT INTO feed_entries(
                id,
                feed_id,
                external_id,
                title,
                published_at,
                summary,
                sanitized_content,
                enclosure_json,
                content_hash,
                fetched_at,
                has_full_content)
            SELECT
                printf('large-%05d', value),
                $feedId,
                printf('large-%05d', value),
                printf('Large entry %d', value),
                $publishedAt,
                '',
                'large retention test',
                '[]',
                printf('hash-%05d', value),
                $fetchedAt,
                0
            FROM sequence;
            """;
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$feedId", FeedId);
        command.Parameters.AddWithValue(
            "$publishedAt",
            Now.AddDays(-181).ToString("O"));
        command.Parameters.AddWithValue("$fetchedAt", Now.ToString("O"));
        Assert.Equal(
            count,
            await command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private static async Task<int> CountFeedEntriesAsync(
        SqliteDatabase database)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM feed_entries;";
        return checked((int)(long)(
            await command.ExecuteScalarAsync(CancellationToken.None)
            ?? throw new InvalidDataException(
                "Feed entry count was unavailable.")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
