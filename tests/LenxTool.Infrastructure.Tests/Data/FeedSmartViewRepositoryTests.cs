using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class FeedSmartViewRepositoryTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools smart view repository tests",
        Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActiveSnapshotRoundTripsAcrossDatabaseRestart()
    {
        FeedSmartViewSnapshot expected = Snapshot(3);
        using (SqliteDatabase database = CreateDatabase())
        {
            await database.InitializeAsync(CancellationToken.None);
            var repository = new FeedSmartViewRepository(database);

            await repository.ReplaceAsync(
                expected,
                CancellationToken.None);

            AssertSnapshot(
                expected,
                await repository.GetAsync(CancellationToken.None));
        }

        using SqliteDatabase reopened = CreateDatabase();
        await reopened.InitializeAsync(CancellationToken.None);
        AssertSnapshot(
            expected,
            await new FeedSmartViewRepository(reopened)
                .GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OlderAndDisabledSnapshotsCannotReplaceLastValidCache()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedSmartViewRepository(database);
        FeedSmartViewSnapshot valid = Snapshot(3);
        await repository.ReplaceAsync(valid, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReplaceAsync(
                Snapshot(2),
                CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            repository.ReplaceAsync(
                Snapshot(4) with
                {
                    Views =
                    [
                        Snapshot(4).Views[0] with
                        {
                            IsEnabled = false
                        }
                    ]
                },
                CancellationToken.None));

        AssertSnapshot(
            valid,
            await repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MarkSynchronizedRequiresExpectedVersion()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new FeedSmartViewRepository(database);
        await repository.ReplaceAsync(
            Snapshot(3),
            CancellationToken.None);

        Assert.False(await repository.MarkSynchronizedAsync(
            2,
            Now.AddMinutes(1),
            CancellationToken.None));
        Assert.True(await repository.MarkSynchronizedAsync(
            3,
            Now.AddMinutes(2),
            CancellationToken.None));

        FeedSmartViewSnapshot restored =
            await repository.GetAsync(CancellationToken.None);
        Assert.Equal(Now.AddMinutes(2), restored.LastSyncedAt);
    }

    private SqliteDatabase CreateDatabase() => new(
        new AppPaths(_testRoot),
        NullLogger<SqliteDatabase>.Instance);

    private static FeedSmartViewSnapshot Snapshot(long version) => new(
        version,
        FeedSmartViewScope.Active,
        Now,
        Now.AddMinutes(1),
        [
            new(
                "30000000-0000-4000-8000-000000000001",
                2,
                "视频收藏",
                20,
                true,
                new(
                    "20000000-0000-4000-8000-000000000001",
                    "10000000-0000-4000-8000-000000000001",
                    EntryViewKind.Video,
                    FeedEntryReadFilter.Unread,
                    true,
                    "release",
                    30))
        ]);

    private static void AssertSnapshot(
        FeedSmartViewSnapshot expected,
        FeedSmartViewSnapshot actual)
    {
        Assert.Equal(expected.ViewSetVersion, actual.ViewSetVersion);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.GeneratedAt, actual.GeneratedAt);
        Assert.Equal(expected.LastSyncedAt, actual.LastSyncedAt);
        Assert.Equal(expected.Views, actual.Views);
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
