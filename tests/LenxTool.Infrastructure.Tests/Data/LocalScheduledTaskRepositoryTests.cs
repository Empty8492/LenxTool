using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class LocalScheduledTaskRepositoryTests : IDisposable
{
    private const string TaskId = "10000000-0000-4000-8000-000000000022";
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools local schedule repository tests",
        Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnabledScheduleRoundTripsAcrossDatabaseRestart()
    {
        LocalScheduledTask expected;
        using (SqliteDatabase database = CreateDatabase())
        {
            await database.InitializeAsync(CancellationToken.None);
            var repository = new LocalScheduledTaskRepository(database);

            expected = await repository.SaveAsync(
                TaskId,
                DailyAtEight(),
                LocalScheduleMissedRunPolicy.RunOnce,
                isEnabled: true,
                Now,
                CancellationToken.None);

            Assert.Equal(
                new DateTimeOffset(2026, 8, 5, 8, 0, 0, TimeSpan.Zero),
                expected.NextRunAtUtc);
            Assert.Equal(expected, await repository.GetAsync(
                TaskId,
                CancellationToken.None));
        }

        using SqliteDatabase reopened = CreateDatabase();
        await reopened.InitializeAsync(CancellationToken.None);
        Assert.Equal(
            expected,
            await new LocalScheduledTaskRepository(reopened).GetAsync(
                TaskId,
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdatingSchedulePreservesCreationAndRecomputesNextRun()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new LocalScheduledTaskRepository(database);
        LocalScheduledTask created = await repository.SaveAsync(
            TaskId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            Now,
            CancellationToken.None);
        var weekly = new LocalScheduleDefinition(
            LocalScheduleFrequency.Weekly,
            "UTC",
            new TimeOnly(9, 30),
            WeeklyDay: DayOfWeek.Monday);

        LocalScheduledTask updated = await repository.SaveAsync(
            TaskId,
            weekly,
            LocalScheduleMissedRunPolicy.Skip,
            isEnabled: true,
            Now.AddHours(2),
            CancellationToken.None);

        Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.Equal(Now.AddHours(2), updated.UpdatedAtUtc);
        Assert.Equal(LocalScheduleMissedRunPolicy.Skip, updated.MissedRunPolicy);
        Assert.Equal(new DateTimeOffset(2026, 8, 10, 9, 30, 0, TimeSpan.Zero),
            updated.NextRunAtUtc);
    }

    [Fact]
    public async Task DisableClearsNextRunAndEnableRecomputesIt()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new LocalScheduledTaskRepository(database);
        await repository.SaveAsync(
            TaskId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            Now,
            CancellationToken.None);

        LocalScheduledTask disabled = Assert.IsType<LocalScheduledTask>(
            await repository.SetEnabledAsync(
                TaskId,
                isEnabled: false,
                Now.AddHours(2),
                CancellationToken.None));
        Assert.False(disabled.IsEnabled);
        Assert.Null(disabled.NextRunAtUtc);

        LocalScheduledTask enabled = Assert.IsType<LocalScheduledTask>(
            await repository.SetEnabledAsync(
                TaskId,
                isEnabled: true,
                Now.AddHours(3),
                CancellationToken.None));
        Assert.True(enabled.IsEnabled);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 6, 8, 0, 0, TimeSpan.Zero),
            enabled.NextRunAtUtc);
    }

    [Fact]
    public async Task GetAllUsesStableCreationOrder()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new LocalScheduledTaskRepository(database);
        const string earlierId = "20000000-0000-4000-8000-000000000022";
        await repository.SaveAsync(
            TaskId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            Now.AddMinutes(1),
            CancellationToken.None);
        await repository.SaveAsync(
            earlierId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.Skip,
            isEnabled: false,
            Now,
            CancellationToken.None);

        IReadOnlyList<LocalScheduledTask> tasks =
            await repository.GetAllAsync(CancellationToken.None);

        Assert.Equal([earlierId, TaskId], tasks.Select(task => task.Id));
        Assert.Null(tasks[0].NextRunAtUtc);
    }

    [Fact]
    public async Task ExpiredOnceCanRemainDisabledButCannotBeEnabled()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new LocalScheduledTaskRepository(database);
        var expired = new LocalScheduleDefinition(
            LocalScheduleFrequency.Once,
            "UTC",
            new TimeOnly(6, 0),
            OnceDate: new DateOnly(2026, 8, 5));
        LocalScheduledTask disabled = await repository.SaveAsync(
            TaskId,
            expired,
            LocalScheduleMissedRunPolicy.Skip,
            isEnabled: false,
            Now,
            CancellationToken.None);

        Assert.False(disabled.IsEnabled);
        Assert.Null(disabled.NextRunAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SetEnabledAsync(
                TaskId,
                isEnabled: true,
                Now.AddHours(1),
                CancellationToken.None));
    }

    [Fact]
    public async Task StaleChangesCannotOverwriteNewerScheduleState()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new LocalScheduledTaskRepository(database);
        await repository.SaveAsync(
            TaskId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            Now.AddHours(2),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync(
                TaskId,
                DailyAtEight() with { LocalTime = new TimeOnly(10, 0) },
                LocalScheduleMissedRunPolicy.Skip,
                isEnabled: true,
                Now.AddHours(1),
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SetEnabledAsync(
                TaskId,
                isEnabled: false,
                Now.AddHours(1),
                CancellationToken.None));

        LocalScheduledTask current = Assert.IsType<LocalScheduledTask>(
            await repository.GetAsync(TaskId, CancellationToken.None));
        Assert.Equal(new TimeOnly(8, 0), current.Schedule.LocalTime);
        Assert.Equal(LocalScheduleMissedRunPolicy.RunOnce, current.MissedRunPolicy);
        Assert.True(current.IsEnabled);
        Assert.Equal(Now.AddHours(2), current.UpdatedAtUtc);
    }

    [Fact]
    public async Task SameTimestampOnlyAllowsIdempotentReplay()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new LocalScheduledTaskRepository(database);
        DateTimeOffset changedAt = Now.AddHours(2);
        LocalScheduledTask created = await repository.SaveAsync(
            TaskId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            changedAt,
            CancellationToken.None);

        Assert.Equal(created, await repository.SaveAsync(
            TaskId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            changedAt,
            CancellationToken.None));
        Assert.Equal(created, await repository.SetEnabledAsync(
            TaskId,
            isEnabled: true,
            changedAt,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync(
                TaskId,
                DailyAtEight() with { LocalTime = new TimeOnly(10, 0) },
                LocalScheduleMissedRunPolicy.RunOnce,
                isEnabled: true,
                changedAt,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SetEnabledAsync(
                TaskId,
                isEnabled: false,
                changedAt,
                CancellationToken.None));

        Assert.Equal(created, await repository.GetAsync(
            TaskId,
            CancellationToken.None));
    }

    [Fact]
    public async Task PayloadRoundTripsAtomicallyAndParticipatesInReplayIdentity()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new LocalScheduledTaskRepository(database);
        DateTimeOffset changedAt = Now.AddHours(2);
        string payload = FeedDigestScopePayload.Serialize(new(
            "10000000-0000-4000-8000-000000000001",
            null,
            "security"));

        LocalScheduledTask created = await repository.SaveAsync(
            TaskId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            changedAt,
            CancellationToken.None,
            payload);

        Assert.Equal(payload, created.Payload);
        Assert.Equal(
            "security",
            FeedDigestScopePayload.Deserialize(created.Payload).SearchText);
        Assert.Equal(created, await repository.SaveAsync(
            TaskId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            changedAt,
            CancellationToken.None,
            payload));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync(
                TaskId,
                DailyAtEight(),
                LocalScheduleMissedRunPolicy.RunOnce,
                isEnabled: true,
                changedAt,
                CancellationToken.None,
                FeedDigestScopePayload.Serialize(
                    FeedDigestScope.AllActive)));

        Assert.Equal(payload, (await repository.GetAsync(
            TaskId,
            CancellationToken.None))!.Payload);
    }

    [Fact]
    public async Task InvalidWritesAndExpiredEnabledOnceAreRejected()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new LocalScheduledTaskRepository(database);
        var expired = new LocalScheduleDefinition(
            LocalScheduleFrequency.Once,
            "UTC",
            new TimeOnly(6, 0),
            OnceDate: new DateOnly(2026, 8, 5));

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(
            "not-a-guid",
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            true,
            Now,
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(
            TaskId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            true,
            Now.ToOffset(TimeSpan.FromHours(8)),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.SaveAsync(
                TaskId,
                DailyAtEight(),
                (LocalScheduleMissedRunPolicy)2,
                true,
                Now,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(
            TaskId,
            expired,
            LocalScheduleMissedRunPolicy.Skip,
            true,
            Now,
            CancellationToken.None));
        Assert.Null(await repository.SetEnabledAsync(
            "20000000-0000-4000-8000-000000000022",
            true,
            Now,
            CancellationToken.None));
    }

    [Fact]
    public async Task SchemaRejectsConflictingShapeAndEnabledState()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_scheduled_tasks(
                id, frequency, time_zone_id, local_time,
                once_date, weekly_day, monthly_day, missed_run_policy,
                is_enabled, next_run_at, created_at, updated_at)
            VALUES(
                '30000000-0000-4000-8000-000000000022', 'MONTHLY', 'UTC', '08:00:00.0000000',
                NULL, NULL, NULL, 'RUN_ONCE',
                1, NULL, '2026-08-05T07:00:00.0000000+00:00',
                '2026-08-05T07:00:00.0000000+00:00');
            """;

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task SchemaRejectsNonUtcScheduleTimestamps()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_scheduled_tasks(
                id, frequency, time_zone_id, local_time,
                once_date, weekly_day, monthly_day, missed_run_policy,
                is_enabled, next_run_at, created_at, updated_at)
            VALUES(
                '40000000-0000-4000-8000-000000000022', 'DAILY', 'UTC', '08:00:00.0000000',
                NULL, NULL, NULL, 'RUN_ONCE',
                1, '2026-08-06T08:00:00.0000000+08:00',
                '2026-08-05T15:00:00.0000000+08:00',
                '2026-08-05T15:00:00.0000000+08:00');
            """;

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    private SqliteDatabase CreateDatabase() => new(
        new AppPaths(_testRoot),
        NullLogger<SqliteDatabase>.Instance);

    private static LocalScheduleDefinition DailyAtEight() => new(
        LocalScheduleFrequency.Daily,
        "UTC",
        new TimeOnly(8, 0));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
