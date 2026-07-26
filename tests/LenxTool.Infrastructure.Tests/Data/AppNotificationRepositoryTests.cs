using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class AppNotificationRepositoryTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools notification repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RegisterAsyncPersistsAndRestoresNotification()
    {
        AppNotification expected = CreateNotification('a', "新模型发布");
        using (SqliteDatabase database = CreateDatabase())
        {
            await database.InitializeAsync(CancellationToken.None);
            var repository = new AppNotificationRepository(database);

            AppNotificationRegistration result = await repository.RegisterAsync(
                expected,
                CancellationToken.None);

            Assert.True(result.Created);
            Assert.Equal(expected, result.Notification);
        }

        using SqliteDatabase reopened = CreateDatabase();
        await reopened.InitializeAsync(CancellationToken.None);
        var restoredRepository = new AppNotificationRepository(reopened);

        Assert.Equal(
            expected,
            Assert.Single(await restoredRepository.GetRecentAsync(
                20,
                CancellationToken.None)));
        Assert.Equal(
            1,
            await restoredRepository.GetUnreadCountAsync(
                CancellationToken.None));
    }

    [Fact]
    public async Task RegisterAsyncSerializesConcurrentDuplicateNotifications()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new AppNotificationRepository(database);
        AppNotification expected = CreateNotification('b', "并发通知");

        AppNotificationRegistration[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => repository.RegisterAsync(
                expected,
                CancellationToken.None)));

        Assert.Single(results, result => result.Created);
        Assert.All(results, result =>
            Assert.Equal(expected, result.Notification));
        Assert.Single(await repository.GetRecentAsync(
            20,
            CancellationToken.None));
    }

    [Fact]
    public async Task RegisterAsyncReturnsOriginalNotificationForDuplicateKey()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new AppNotificationRepository(database);
        AppNotification original = CreateNotification('c', "原通知");
        await repository.RegisterAsync(original, CancellationToken.None);

        AppNotificationRegistration duplicate = await repository.RegisterAsync(
            original with
            {
                Title = "不应覆盖",
                CreatedAt = original.CreatedAt.AddMinutes(1)
            },
            CancellationToken.None);

        Assert.False(duplicate.Created);
        Assert.Equal(original, duplicate.Notification);
    }

    [Fact]
    public async Task RecentOrderingAndReadOperationsRemainConsistent()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new AppNotificationRepository(database);
        AppNotification older = CreateNotification('d', "较早");
        AppNotification newer = CreateNotification('e', "较新") with
        {
            CreatedAt = older.CreatedAt.AddMinutes(1)
        };
        await repository.RegisterAsync(older, CancellationToken.None);
        await repository.RegisterAsync(newer, CancellationToken.None);

        IReadOnlyList<AppNotification> initial = await repository.GetRecentAsync(
            20,
            CancellationToken.None);
        Assert.Equal([newer.Id, older.Id], initial.Select(item => item.Id));
        DateTimeOffset firstReadAt = newer.CreatedAt.AddMinutes(2);
        Assert.True(await repository.MarkReadAsync(
            newer.Id,
            firstReadAt,
            CancellationToken.None));
        Assert.False(await repository.MarkReadAsync(
            new string('f', 64),
            firstReadAt,
            CancellationToken.None));
        Assert.Equal(
            1,
            await repository.GetUnreadCountAsync(CancellationToken.None));

        DateTimeOffset allReadAt = firstReadAt.AddMinutes(1);
        Assert.Equal(
            1,
            await repository.MarkAllReadAsync(
                allReadAt,
                CancellationToken.None));
        Assert.Equal(
            0,
            await repository.GetUnreadCountAsync(CancellationToken.None));
        IReadOnlyList<AppNotification> completed = await repository.GetRecentAsync(
            20,
            CancellationToken.None);
        Assert.Equal(firstReadAt, completed[0].ReadAt);
        Assert.Equal(allReadAt, completed[1].ReadAt);
    }

    private SqliteDatabase CreateDatabase() => new(
        new AppPaths(_testRoot),
        NullLogger<SqliteDatabase>.Instance);

    private static AppNotification CreateNotification(
        char key,
        string title) => new(
        new string(key, 64),
        "entry-notification",
        "30000000-0000-4000-8000-000000000701",
        "40000000-0000-4000-8000-000000000701",
        3,
        title,
        "AI 资讯",
        new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero),
        null);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
