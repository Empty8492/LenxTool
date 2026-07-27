using System.Security.Cryptography;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class EntryAssetStoreTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools entry asset store tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PutPersistsHashMetadataAndPromotesAtomicCacheFile()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = CreatePaths();
        var store = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 256, MaximumAssetBytes: 64));
        byte[] content = "中文图片内容"u8.ToArray();

        EntryAsset asset = await store.PutAsync(
            "entry-1",
            "https://cdn.example.test/image.png",
            "image/png",
            new MemoryStream(content),
            CancellationToken.None);

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            asset.ContentHash);
        Assert.Equal(content.LongLength, asset.SizeBytes);
        EntryAsset? storedBeforeRead = await store.GetAsync(
            asset.EntryId,
            asset.SourceUrl,
            CancellationToken.None);
        Assert.Equal(asset, storedBeforeRead);
        Stream? cachedStream = await store.OpenReadAsync(
            asset,
            CancellationToken.None);
        Assert.IsType<FileStream>(cachedStream);
        Assert.Equal(content, await ReadAllAsync(cachedStream));
        EntryAsset? storedAfterRead = await store.GetAsync(
            asset.EntryId,
            asset.SourceUrl,
            CancellationToken.None);
        Assert.Equal(asset with { LastAccessedAt = storedAfterRead!.LastAccessedAt }, storedAfterRead);
        Assert.Single(Directory.GetFiles(paths.AssetCacheDirectory));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(paths.AssetCacheDirectory),
            path => Path.GetExtension(path) == ".tmp");
    }

    [Fact]
    public async Task RejectsSingleAssetOverLimitWithoutLeavingDatabaseOrTempFile()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = CreatePaths();
        var store = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 256, MaximumAssetBytes: 4));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.PutAsync(
                "entry-oversized",
                "https://cdn.example.test/large.png",
                "image/png",
                new MemoryStream(new byte[5]),
                CancellationToken.None));

        Assert.Null(await store.GetAsync(
            "entry-oversized",
            "https://cdn.example.test/large.png",
            CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(paths.AssetCacheDirectory));
    }

    [Fact]
    public async Task PruneRemovesLeastRecentlyUsedUnprotectedContentHash()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = CreatePaths();
        var store = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 10, MaximumAssetBytes: 10));

        EntryAsset first = await store.PutAsync(
            "entry-first",
            "https://cdn.example.test/first.png",
            "image/png",
            new MemoryStream([1, 2, 3, 4]),
            CancellationToken.None);
        EntryAsset second = await store.PutAsync(
            "entry-second",
            "https://cdn.example.test/second.png",
            "image/png",
            new MemoryStream([5, 6, 7, 8]),
            CancellationToken.None);
        _ = await store.OpenReadAsync(first, CancellationToken.None);
        EntryAsset third = await store.PutAsync(
            "entry-third",
            "https://cdn.example.test/third.png",
            "image/png",
            new MemoryStream([9, 10, 11, 12]),
            CancellationToken.None);

        Assert.NotNull(await store.GetAsync(first.EntryId, first.SourceUrl, CancellationToken.None));
        Assert.Null(await store.GetAsync(second.EntryId, second.SourceUrl, CancellationToken.None));
        Assert.NotNull(await store.GetAsync(third.EntryId, third.SourceUrl, CancellationToken.None));
    }

    [Fact]
    public async Task CorruptedFileIsDetectedAndRemovedFromIndex()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = CreatePaths();
        var store = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 256, MaximumAssetBytes: 64));
        EntryAsset asset = await store.PutAsync(
            "entry-corrupt",
            "https://cdn.example.test/corrupt.png",
            "image/png",
            new MemoryStream([1, 2, 3]),
            CancellationToken.None);
        string path = Path.Combine(paths.AssetCacheDirectory, asset.ContentHash);
        await File.WriteAllBytesAsync(path, [9, 9, 9]);

        Assert.Null(await store.OpenReadAsync(asset, CancellationToken.None));
        Assert.Null(await store.GetAsync(asset.EntryId, asset.SourceUrl, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RemoveUnreferencedFilesDeletesOnlyContentOutsideTheIndex()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        AppPaths paths = CreatePaths();
        var store = new EntryAssetStore(
            database,
            paths,
            new(MaximumBytes: 256, MaximumAssetBytes: 64));
        EntryAsset referenced = await store.PutAsync(
            "entry-referenced",
            "https://cdn.example.test/referenced.png",
            "image/png",
            new MemoryStream([1, 2, 3]),
            CancellationToken.None);
        EntryAsset orphaned = await store.PutAsync(
            "entry-orphaned",
            "https://cdn.example.test/orphaned.png",
            "image/png",
            new MemoryStream([4, 5, 6, 7]),
            CancellationToken.None);
        await using (SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand removeIndex = connection.CreateCommand())
        {
            removeIndex.CommandText = """
                DELETE FROM entry_assets
                WHERE entry_id='entry-orphaned';
                """;
            await removeIndex.ExecuteNonQueryAsync(CancellationToken.None);
        }

        EntryAssetPruneResult result =
            await store.RemoveUnreferencedFilesAsync(CancellationToken.None);

        Assert.Equal(1, result.RemovedFileCount);
        Assert.Equal(4, result.RemovedBytes);
        Assert.True(File.Exists(
            Path.Combine(paths.AssetCacheDirectory, referenced.ContentHash)));
        Assert.False(File.Exists(
            Path.Combine(paths.AssetCacheDirectory, orphaned.ContentHash)));
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        return database;
    }

    private AppPaths CreatePaths() => new(_testRoot);

    private static async Task<byte[]> ReadAllAsync(Stream? stream)
    {
        Assert.NotNull(stream);
        await using (stream)
        {
            using var destination = new MemoryStream();
            await stream.CopyToAsync(destination);
            return destination.ToArray();
        }
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
