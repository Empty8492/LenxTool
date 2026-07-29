using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.Infrastructure.Tests.Data;

public sealed class EntryExportTaskRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] SensitiveColumnFragments =
    [
        "password", "credential", "secret", "api_key",
        "access_token", "refresh_token", "request_body",
        "response_body", "content_body"
    ];
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools entry export queue repository tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnqueueIsIdempotentAndHistoryContainsOnlySafeMetadata()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new EntryExportTaskRepository(database);
        EntryExportRequest request = Request();

        EntryExportEnqueueResult first = await repository.EnqueueAsync(
            request,
            Now,
            CancellationToken.None);
        EntryExportEnqueueResult duplicate = await repository.EnqueueAsync(
            request,
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Task, duplicate.Task);
        EntryExportTask task = Assert.Single(
            await repository.GetRecentAsync(10, CancellationToken.None));
        Assert.Equal(EntryExportTaskStatus.Queued, task.Status);
        Assert.Equal(request.IdempotencyKey, task.IdempotencyKey);
        Assert.Equal(request.Entry.Id, task.EntryId);
        Assert.Equal(request.Entry.ContentHash, task.ContentHash);
        Assert.Null(task.LastErrorCode);

        await using SqliteConnection connection = await database.OpenConnectionAsync(
            CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(entry_export_tasks);";
        var columnNames = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            columnNames.Add(reader.GetString(1));
        }
        Assert.DoesNotContain(
            columnNames,
            name => SensitiveColumnFragments.Any(fragment => name.Contains(
                fragment,
                StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task RetrySurvivesRestartAndBecomesClaimableAtDueTime()
    {
        using (SqliteDatabase database = await CreateDatabaseAsync())
        {
            var repository = new EntryExportTaskRepository(database);
            await repository.EnqueueAsync(
                Request(),
                Now,
                CancellationToken.None);
            EntryExportTaskLease lease = Assert.IsType<EntryExportTaskLease>(
                await repository.ClaimDueAsync(
                    Now,
                    TimeSpan.FromMinutes(5),
                    CancellationToken.None));
            await repository.ScheduleRetryAsync(
                lease,
                EntryExportTaskErrorCode.RateLimited,
                Now.AddSeconds(47),
                Now.AddSeconds(1),
                CancellationToken.None);
        }

        using SqliteDatabase reopened = await CreateDatabaseAsync();
        var reopenedRepository = new EntryExportTaskRepository(reopened);
        Assert.Null(await reopenedRepository.ClaimDueAsync(
            Now.AddSeconds(46),
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        EntryExportTaskLease retry = Assert.IsType<EntryExportTaskLease>(
            await reopenedRepository.ClaimDueAsync(
                Now.AddSeconds(47),
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        Assert.Equal(2, retry.AttemptCount);
        await reopenedRepository.CompleteAsync(
            retry,
            Now.AddMinutes(1),
            CancellationToken.None);

        EntryExportTask completed = Assert.IsType<EntryExportTask>(
            await reopenedRepository.GetAsync(
                retry.IdempotencyKey,
                CancellationToken.None));
        Assert.Equal(EntryExportTaskStatus.Completed, completed.Status);
        Assert.Null(completed.NextAttemptAt);
    }

    [Fact]
    public async Task ConcurrentClaimsIssueOneLeaseAndExpiredLeaseCannotCommit()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var firstRepository = new EntryExportTaskRepository(database);
        var secondRepository = new EntryExportTaskRepository(database);
        await firstRepository.EnqueueAsync(
            Request(),
            Now,
            CancellationToken.None);

        EntryExportTaskLease?[] claims = await Task.WhenAll(
            firstRepository.ClaimDueAsync(
                Now,
                TimeSpan.FromMinutes(5),
                CancellationToken.None),
            secondRepository.ClaimDueAsync(
                Now,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));

        EntryExportTaskLease expired = Assert.Single(
            claims.OfType<EntryExportTaskLease>());
        Assert.True(await firstRepository.RenewLeaseAsync(
            expired,
            Now.AddMinutes(4),
            Now.AddMinutes(9),
            CancellationToken.None));
        Assert.Null(await secondRepository.ClaimDueAsync(
            Now.AddMinutes(6),
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        EntryExportTaskLease current = Assert.IsType<EntryExportTaskLease>(
            await secondRepository.ClaimDueAsync(
                Now.AddMinutes(10),
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        Assert.NotEqual(expired.LeaseToken, current.LeaseToken);
        Assert.Equal(2, current.AttemptCount);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstRepository.CompleteAsync(
                expired,
                Now.AddMinutes(10),
                CancellationToken.None));
        await secondRepository.CompleteAsync(
            current,
            Now.AddMinutes(11),
            CancellationToken.None);
    }

    [Fact]
    public async Task CancellationHandlesQueuedAndRunningTasksWithoutFreeTextErrors()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new EntryExportTaskRepository(database);
        EntryExportEnqueueResult queued = await repository.EnqueueAsync(
            Request(),
            Now,
            CancellationToken.None);

        Assert.Equal(
            EntryExportCancellationResult.Cancelled,
            await repository.RequestCancellationAsync(
                queued.Task.IdempotencyKey,
                Now.AddMinutes(1),
                CancellationToken.None));
        Assert.Equal(
            EntryExportTaskStatus.Cancelled,
            (await repository.GetAsync(
                queued.Task.IdempotencyKey,
                CancellationToken.None))?.Status);

        EntryExportEnqueueResult running = await repository.EnqueueAsync(
            Request(contentHash: new string('b', 64)),
            Now.AddMinutes(2),
            CancellationToken.None);
        EntryExportTaskLease lease = Assert.IsType<EntryExportTaskLease>(
            await repository.ClaimDueAsync(
                Now.AddMinutes(2),
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        Assert.Equal(
            EntryExportCancellationResult.CancellationRequested,
            await repository.RequestCancellationAsync(
                running.Task.IdempotencyKey,
                Now.AddMinutes(3),
                CancellationToken.None));
        Assert.True(await repository.IsCancellationRequestedAsync(
            lease,
            CancellationToken.None));
        await repository.CancelClaimedAsync(
            lease,
            Now.AddMinutes(3),
            CancellationToken.None);

        EntryExportTask cancelled = Assert.IsType<EntryExportTask>(
            await repository.GetAsync(
                running.Task.IdempotencyKey,
                CancellationToken.None));
        Assert.Equal(EntryExportTaskStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.LastErrorCode);
    }

    [Fact]
    public async Task RetryingTaskCancellationSurvivesCrashAndLeaseExpiry()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new EntryExportTaskRepository(database);
        EntryExportEnqueueResult enqueued = await repository.EnqueueAsync(
            Request(),
            Now,
            CancellationToken.None);
        EntryExportTaskLease firstLease = Assert.IsType<EntryExportTaskLease>(
            await repository.ClaimDueAsync(
                Now,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        await repository.ScheduleRetryAsync(
            firstLease,
            EntryExportTaskErrorCode.RateLimited,
            Now.AddMinutes(1),
            Now.AddSeconds(10),
            CancellationToken.None);

        EntryExportTaskLease retryLease = Assert.IsType<EntryExportTaskLease>(
            await repository.ClaimDueAsync(
                Now.AddMinutes(1),
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        Assert.Equal(
            EntryExportCancellationResult.CancellationRequested,
            await repository.RequestCancellationAsync(
                enqueued.Task.IdempotencyKey,
                Now.AddMinutes(2),
                CancellationToken.None));

        // 不主动结束租约来模拟进程崩溃；下次轮询必须把取消请求安全收敛为终态，
        // 不能让上一次重试错误违反数据库约束并阻塞整个队列。
        Assert.Null(await repository.ClaimDueAsync(
            Now.AddMinutes(6).AddSeconds(1),
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        EntryExportTask cancelled = Assert.IsType<EntryExportTask>(
            await repository.GetAsync(
                enqueued.Task.IdempotencyKey,
                CancellationToken.None));
        Assert.Equal(EntryExportTaskStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.LastErrorCode);
        Assert.NotNull(cancelled.CompletedAt);
    }

    [Fact]
    public async Task SchemaRejectsImpossibleLifecycleShapes()
    {
        using SqliteDatabase database = await CreateDatabaseAsync();
        var repository = new EntryExportTaskRepository(database);
        EntryExportEnqueueResult queued = await repository.EnqueueAsync(
            Request(),
            Now,
            CancellationToken.None);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.Parameters.AddWithValue(
            "$idempotencyKey",
            queued.Task.IdempotencyKey);
        command.Parameters.AddWithValue(
            "$timestamp",
            Now.AddMinutes(1).ToString("O"));
        command.Parameters.AddWithValue(
            "$leaseToken",
            Guid.NewGuid().ToString());

        command.CommandText = """
            UPDATE entry_export_tasks
            SET status='RUNNING',
                next_attempt_at=NULL,
                lease_token=$leaseToken,
                lease_expires_at=$timestamp
            WHERE idempotency_key=$idempotencyKey;
            """;
        await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));

        command.CommandText = """
            UPDATE entry_export_tasks
            SET status='COMPLETED',
                attempt_count=1,
                next_attempt_at=NULL,
                last_error_code='RateLimited',
                completed_at=$timestamp
            WHERE idempotency_key=$idempotencyKey;
            """;
        await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));

        command.CommandText = """
            UPDATE entry_export_tasks
            SET status='FAILED',
                attempt_count=1,
                next_attempt_at=NULL,
                last_error_code=NULL,
                completed_at=$timestamp
            WHERE idempotency_key=$idempotencyKey;
            """;
        await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        ClearTestPool();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync()
    {
        var database = new SqliteDatabase(
            new AppPaths(_testRoot),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        return database;
    }

    private void ClearTestPool()
    {
        AppPaths paths = new(_testRoot);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5
        };
        using var connection = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private static EntryExportRequest Request(
        string? contentHash = null)
    {
        FeedEntry entry = new(
            "entry-export-42",
            "30000000-0000-4000-8000-000000000001",
            "external-export-42",
            "https://example.com/articles/42",
            "导出队列测试",
            "作者",
            Now.AddDays(-1),
            null,
            "摘要",
            "<p>正文</p>",
            ["RSS"],
            [],
            contentHash ?? new string('a', 64),
            Now);
        return EntryExportRequest.Create(
            "markdown",
            "knowledge-base",
            entry,
            EntryViewKind.Article,
            128);
    }
}
