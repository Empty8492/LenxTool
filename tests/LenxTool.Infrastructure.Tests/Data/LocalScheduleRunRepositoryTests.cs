using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class LocalScheduleRunRepositoryTests : IDisposable
{
    private const string TaskId = "10000000-0000-4000-8000-000000000023";
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools local schedule run repository tests",
        Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 5, 7, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task DueWindowIsClaimedOnceAndAdvancesCursor()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var firstRepository = new LocalScheduleRunRepository(database);
        var secondRepository = new LocalScheduleRunRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);

        LocalScheduleRunLease lease = Assert.IsType<LocalScheduleRunLease>(
            await firstRepository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));

        Assert.Equal(TaskId, lease.ScheduleId);
        Assert.Equal(dueAt, lease.ScheduledForUtc);
        Assert.Equal(1, lease.AttemptCount);
        Assert.Null(await secondRepository.ClaimDueAsync(
            dueAt,
            dueAt.AddMinutes(-1),
            LeaseDuration,
            CancellationToken.None));
        LocalScheduledTask task = Assert.IsType<LocalScheduledTask>(
            await new LocalScheduledTaskRepository(database).GetAsync(
                TaskId,
                CancellationToken.None));
        Assert.Equal(dueAt.AddDays(1), task.NextRunAtUtc);

        LocalScheduleRun run = Assert.Single(await firstRepository.GetRecentAsync(
            TaskId,
            10,
            CancellationToken.None));
        Assert.Equal(LocalScheduleRunStatus.Running, run.Status);
        Assert.Equal(1, run.AttemptCount);
    }

    [Fact]
    public async Task MissedRunOnceClaimsOnlyPersistedWindowAndSkipsBacklog()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset recoveredAt = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        LocalScheduleRunLease lease = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                recoveredAt,
                recoveredAt,
                LeaseDuration,
                CancellationToken.None));

        Assert.Equal(CreatedAt.AddHours(1), lease.ScheduledForUtc);
        await repository.CompleteAsync(
            lease,
            recoveredAt.AddMinutes(1),
            CancellationToken.None);
        Assert.Null(await repository.ClaimDueAsync(
            recoveredAt.AddMinutes(2),
            recoveredAt,
            LeaseDuration,
            CancellationToken.None));
        LocalScheduledTask task = Assert.IsType<LocalScheduledTask>(
            await new LocalScheduledTaskRepository(database).GetAsync(
                TaskId,
                CancellationToken.None));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero),
            task.NextRunAtUtc);
        Assert.Single(await repository.GetRecentAsync(
            TaskId,
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task MissedSkipAdvancesCursorWithoutCreatingRun()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.Skip);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset recoveredAt = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(await repository.ClaimDueAsync(
            recoveredAt,
            recoveredAt,
            LeaseDuration,
            CancellationToken.None));

        LocalScheduledTask task = Assert.IsType<LocalScheduledTask>(
            await new LocalScheduledTaskRepository(database).GetAsync(
                TaskId,
                CancellationToken.None));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero),
            task.NextRunAtUtc);
        Assert.Empty(await repository.GetRecentAsync(
            TaskId,
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task WindowEqualToMissedBoundaryIsDueRatherThanSkipped()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.Skip);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);

        LocalScheduleRunLease lease = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                dueAt,
                dueAt,
                LeaseDuration,
                CancellationToken.None));

        Assert.Equal(dueAt, lease.ScheduledForUtc);
        Assert.Equal(
            LocalScheduleRunStatus.Running,
            Assert.Single(await repository.GetRecentAsync(
                TaskId,
                10,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ExpiredLeaseIsReclaimedAndStaleOwnerCannotFinish()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var firstRepository = new LocalScheduleRunRepository(database);
        var secondRepository = new LocalScheduleRunRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        LocalScheduleRunLease first = Assert.IsType<LocalScheduleRunLease>(
            await firstRepository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));

        LocalScheduleRunLease second = Assert.IsType<LocalScheduleRunLease>(
            await secondRepository.ClaimDueAsync(
                dueAt.Add(LeaseDuration),
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));

        Assert.Equal(first.ScheduledForUtc, second.ScheduledForUtc);
        Assert.Equal(2, second.AttemptCount);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstRepository.CompleteAsync(
                first,
                dueAt.Add(LeaseDuration).AddMinutes(1),
                CancellationToken.None));
        await secondRepository.CompleteAsync(
            second,
            dueAt.Add(LeaseDuration).AddMinutes(1),
            CancellationToken.None);
        LocalScheduleRun run = Assert.Single(await secondRepository.GetRecentAsync(
            TaskId,
            10,
            CancellationToken.None));
        Assert.Equal(LocalScheduleRunStatus.Completed, run.Status);
        Assert.Equal(2, run.AttemptCount);
    }

    [Fact]
    public async Task ExpiredOwnerCannotRenewOrFinishBeforeAnotherClaim()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        LocalScheduleRunLease expired = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));
        DateTimeOffset expiredAt = dueAt.Add(LeaseDuration);

        Assert.False(await repository.RenewLeaseAsync(
            expired,
            expiredAt,
            expiredAt.Add(LeaseDuration),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CompleteAsync(
                expired,
                expiredAt,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CancelAsync(
                expired,
                expiredAt,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReleaseAsync(
                expired,
                expiredAt,
                CancellationToken.None));

        LocalScheduleRunLease reclaimed = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                expiredAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.NotEqual(expired.LeaseToken, reclaimed.LeaseToken);
    }

    [Fact]
    public async Task ReleasedWindowCanBeReclaimedAfterDatabaseRestart()
    {
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        LocalScheduleRunLease first;
        using (SqliteDatabase database = CreateDatabase())
        {
            await database.InitializeAsync(CancellationToken.None);
            await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
            var repository = new LocalScheduleRunRepository(database);
            first = Assert.IsType<LocalScheduleRunLease>(
                await repository.ClaimDueAsync(
                    dueAt,
                    dueAt.AddMinutes(-1),
                    LeaseDuration,
                    CancellationToken.None));
            await repository.ReleaseAsync(
                first,
                dueAt.AddMinutes(1),
                CancellationToken.None);
        }

        using SqliteDatabase reopened = CreateDatabase();
        await reopened.InitializeAsync(CancellationToken.None);
        var reopenedRepository = new LocalScheduleRunRepository(reopened);
        LocalScheduleRunLease reclaimed = Assert.IsType<LocalScheduleRunLease>(
            await reopenedRepository.ClaimDueAsync(
                dueAt.AddMinutes(2),
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));
        Assert.Equal(first.ScheduledForUtc, reclaimed.ScheduledForUtc);
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.NotEqual(first.LeaseToken, reclaimed.LeaseToken);
    }

    [Fact]
    public async Task RunOnceOnceScheduleDisablesButItsExpiredRunIsRecoverable()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        DateTimeOffset scheduledFor = CreatedAt.AddHours(1);
        await SaveOnceAsync(
            database,
            LocalScheduleMissedRunPolicy.RunOnce,
            scheduledFor);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset recoveredAt = scheduledFor.AddDays(1);

        LocalScheduleRunLease first = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                recoveredAt,
                recoveredAt,
                LeaseDuration,
                CancellationToken.None));
        LocalScheduledTask disabled = Assert.IsType<LocalScheduledTask>(
            await new LocalScheduledTaskRepository(database).GetAsync(
                TaskId,
                CancellationToken.None));
        Assert.False(disabled.IsEnabled);
        Assert.Null(disabled.NextRunAtUtc);

        LocalScheduleRunLease reclaimed = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                recoveredAt.Add(LeaseDuration),
                recoveredAt,
                LeaseDuration,
                CancellationToken.None));
        Assert.Equal(first.ScheduledForUtc, reclaimed.ScheduledForUtc);
        Assert.Equal(2, reclaimed.AttemptCount);
    }

    [Fact]
    public async Task SkipOnceScheduleDisablesWithoutCreatingRun()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        DateTimeOffset scheduledFor = CreatedAt.AddHours(1);
        await SaveOnceAsync(
            database,
            LocalScheduleMissedRunPolicy.Skip,
            scheduledFor);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset recoveredAt = scheduledFor.AddDays(1);

        Assert.Null(await repository.ClaimDueAsync(
            recoveredAt,
            recoveredAt,
            LeaseDuration,
            CancellationToken.None));

        LocalScheduledTask disabled = Assert.IsType<LocalScheduledTask>(
            await new LocalScheduledTaskRepository(database).GetAsync(
                TaskId,
                CancellationToken.None));
        Assert.False(disabled.IsEnabled);
        Assert.Null(disabled.NextRunAtUtc);
        Assert.Empty(await repository.GetRecentAsync(
            TaskId,
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentRepositoriesClaimSingleLogicalWindow()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        var firstRepository = new LocalScheduleRunRepository(database);
        var secondRepository = new LocalScheduleRunRepository(database);

        LocalScheduleRunLease?[] results = await Task.WhenAll(
            firstRepository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None),
            secondRepository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));

        Assert.Single(results, result => result is not null);
        Assert.Single(await firstRepository.GetRecentAsync(
            TaskId,
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task CancelledWindowIsTerminalAndRenewHonorsCurrentLease()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        LocalScheduleRunLease lease = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));

        Assert.True(await repository.RenewLeaseAsync(
            lease,
            dueAt.AddMinutes(1),
            dueAt.AddMinutes(11),
            CancellationToken.None));
        await repository.CancelAsync(
            lease,
            dueAt.AddMinutes(2),
            CancellationToken.None);
        Assert.False(await repository.RenewLeaseAsync(
            lease,
            dueAt.AddMinutes(3),
            dueAt.AddMinutes(13),
            CancellationToken.None));
        Assert.Null(await repository.ClaimDueAsync(
            dueAt.AddMinutes(20),
            dueAt.AddMinutes(-1),
            LeaseDuration,
            CancellationToken.None));
        Assert.Equal(
            LocalScheduleRunStatus.Cancelled,
            Assert.Single(await repository.GetRecentAsync(
                TaskId,
                10,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ScheduleMutationRequestsCancellationAndRejectsCompletion()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var runs = new LocalScheduleRunRepository(database);
        var schedules = new LocalScheduledTaskRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        LocalScheduleRunLease lease = Assert.IsType<LocalScheduleRunLease>(
            await runs.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));
        DateTimeOffset disabledAt = dueAt.AddMinutes(1);

        await schedules.SetEnabledAsync(
            TaskId,
            isEnabled: false,
            disabledAt,
            CancellationToken.None);

        Assert.True(await runs.IsCancellationRequestedAsync(
            lease,
            disabledAt,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runs.CompleteAsync(
                lease,
                disabledAt,
                CancellationToken.None));
        await runs.CancelAsync(
            lease,
            disabledAt,
            CancellationToken.None);
        Assert.Equal(
            LocalScheduleRunStatus.Cancelled,
            Assert.Single(await runs.GetRecentAsync(
                TaskId,
                10,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task DisableThenReenableStillCancelsAlreadyClaimedWindow()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var runs = new LocalScheduleRunRepository(database);
        var schedules = new LocalScheduledTaskRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        LocalScheduleRunLease lease = Assert.IsType<LocalScheduleRunLease>(
            await runs.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));

        await schedules.SetEnabledAsync(
            TaskId,
            isEnabled: false,
            dueAt.AddMinutes(1),
            CancellationToken.None);
        await schedules.SetEnabledAsync(
            TaskId,
            isEnabled: true,
            dueAt.AddMinutes(2),
            CancellationToken.None);

        Assert.True(await runs.IsCancellationRequestedAsync(
            lease,
            dueAt.AddMinutes(2),
            CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredWindowCancelledByScheduleMutationIsNotReclaimed()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var runs = new LocalScheduleRunRepository(database);
        var schedules = new LocalScheduledTaskRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        _ = Assert.IsType<LocalScheduleRunLease>(
            await runs.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));
        await schedules.SetEnabledAsync(
            TaskId,
            isEnabled: false,
            dueAt.AddMinutes(1),
            CancellationToken.None);

        Assert.Null(await runs.ClaimDueAsync(
            dueAt.Add(LeaseDuration),
            dueAt.AddMinutes(-1),
            LeaseDuration,
            CancellationToken.None));
        Assert.Equal(
            LocalScheduleRunStatus.Cancelled,
            Assert.Single(await runs.GetRecentAsync(
                TaskId,
                10,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task MissingScheduleRequestsCancellationAndRejectsNonCancelFinish()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        LocalScheduleRunLease lease = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));
        await DeleteScheduleAsync(database);
        DateTimeOffset observedAt = dueAt.AddMinutes(1);

        Assert.True(await repository.IsCancellationRequestedAsync(
            lease,
            observedAt,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CompleteAsync(
                lease,
                observedAt,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReleaseAsync(
                lease,
                observedAt,
                CancellationToken.None));
        await repository.CancelAsync(
            lease,
            observedAt,
            CancellationToken.None);
    }

    [Fact]
    public async Task ExpiredOrphanWindowIsCancelledInsteadOfReclaimed()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        _ = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));
        await DeleteScheduleAsync(database);

        Assert.Null(await repository.ClaimDueAsync(
            dueAt.Add(LeaseDuration),
            dueAt.AddMinutes(-1),
            LeaseDuration,
            CancellationToken.None));
        Assert.Equal(
            LocalScheduleRunStatus.Cancelled,
            Assert.Single(await repository.GetRecentAsync(
                TaskId,
                10,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task EligibleScheduleFilterDoesNotClaimUnknownHandlers()
    {
        const string eligibleId =
            "10000000-0000-4000-8000-000000000024";
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        await new LocalScheduledTaskRepository(database).SaveAsync(
            eligibleId,
            DailyAtEight(),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled: true,
            CreatedAt,
            CancellationToken.None);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);

        LocalScheduleRunLease lease = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                [eligibleId],
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));

        Assert.Equal(eligibleId, lease.ScheduleId);
        Assert.Empty(await repository.GetRecentAsync(
            TaskId,
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task RenewLeaseIsMonotonicAndSameTimestampIsIdempotent()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        var repository = new LocalScheduleRunRepository(database);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        LocalScheduleRunLease lease = Assert.IsType<LocalScheduleRunLease>(
            await repository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));
        DateTimeOffset renewedAt = dueAt.AddMinutes(1);
        DateTimeOffset extendedUntil = dueAt.AddMinutes(20);

        Assert.True(await repository.RenewLeaseAsync(
            lease,
            renewedAt,
            extendedUntil,
            CancellationToken.None));
        Assert.True(await repository.RenewLeaseAsync(
            lease,
            renewedAt,
            extendedUntil,
            CancellationToken.None));
        Assert.False(await repository.RenewLeaseAsync(
            lease,
            renewedAt,
            extendedUntil.AddMinutes(1),
            CancellationToken.None));
        Assert.False(await repository.RenewLeaseAsync(
            lease,
            renewedAt.AddMinutes(1),
            extendedUntil.AddMinutes(-1),
            CancellationToken.None));
        Assert.True(await repository.RenewLeaseAsync(
            lease,
            renewedAt.AddMinutes(1),
            extendedUntil.AddMinutes(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task InvalidArgumentsAndImpossibleStoredStateAreRejected()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var repository = new LocalScheduleRunRepository(database);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.ClaimDueAsync(
                CreatedAt.ToOffset(TimeSpan.FromHours(8)),
                CreatedAt,
                LeaseDuration,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.ClaimDueAsync(
                CreatedAt,
                CreatedAt.AddMinutes(1),
                LeaseDuration,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.GetRecentAsync(TaskId, 0, CancellationToken.None));

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_schedule_runs(
                schedule_id, scheduled_for, status, attempt_count,
                lease_token, lease_expires_at, created_at, updated_at,
                completed_at)
            VALUES(
                '10000000-0000-4000-8000-000000000023',
                '2026-08-05T08:00:00.0000000+00:00',
                'RUNNING', 1, NULL, NULL,
                '2026-08-05T08:00:00.0000000+00:00',
                '2026-08-05T08:00:00.0000000+00:00', NULL);
            """;
        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task RunInsertFailureRollsBackScheduleCursorAdvance()
    {
        using SqliteDatabase database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await SaveDailyAsync(database, LocalScheduleMissedRunPolicy.RunOnce);
        DateTimeOffset dueAt = CreatedAt.AddHours(1);
        await using (SqliteConnection connection =
                     await database.OpenConnectionAsync(CancellationToken.None))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_local_schedule_run
                BEFORE INSERT ON local_schedule_runs
                BEGIN
                    SELECT RAISE(ABORT, 'simulated run insert failure');
                END;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        var repository = new LocalScheduleRunRepository(database);

        await Assert.ThrowsAsync<SqliteException>(() =>
            repository.ClaimDueAsync(
                dueAt,
                dueAt.AddMinutes(-1),
                LeaseDuration,
                CancellationToken.None));

        LocalScheduledTask unchanged = Assert.IsType<LocalScheduledTask>(
            await new LocalScheduledTaskRepository(database).GetAsync(
                TaskId,
                CancellationToken.None));
        Assert.Equal(dueAt, unchanged.NextRunAtUtc);
        Assert.Empty(await repository.GetRecentAsync(
            TaskId,
            10,
            CancellationToken.None));
    }

    private static async Task SaveDailyAsync(
        SqliteDatabase database,
        LocalScheduleMissedRunPolicy policy)
    {
        await new LocalScheduledTaskRepository(database).SaveAsync(
            TaskId,
            new LocalScheduleDefinition(
                LocalScheduleFrequency.Daily,
                "UTC",
                new TimeOnly(8, 0)),
            policy,
            isEnabled: true,
            CreatedAt,
            CancellationToken.None);
    }

    private static async Task SaveOnceAsync(
        SqliteDatabase database,
        LocalScheduleMissedRunPolicy policy,
        DateTimeOffset scheduledFor)
    {
        await new LocalScheduledTaskRepository(database).SaveAsync(
            TaskId,
            new LocalScheduleDefinition(
                LocalScheduleFrequency.Once,
                "UTC",
                TimeOnly.FromDateTime(scheduledFor.UtcDateTime),
                OnceDate: DateOnly.FromDateTime(scheduledFor.UtcDateTime)),
            policy,
            isEnabled: true,
            CreatedAt,
            CancellationToken.None);
    }

    private static LocalScheduleDefinition DailyAtEight() => new(
        LocalScheduleFrequency.Daily,
        "UTC",
        new TimeOnly(8, 0));

    private static async Task DeleteScheduleAsync(SqliteDatabase database)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM local_scheduled_tasks WHERE id=$scheduleId;";
        command.Parameters.AddWithValue("$scheduleId", TaskId);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private SqliteDatabase CreateDatabase() => new(
        new AppPaths(_testRoot),
        NullLogger<SqliteDatabase>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
