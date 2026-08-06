using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

/// <summary>
/// 为不支持服务端幂等键的模型调用提供本地 at-most-once 边界。STARTED 在
/// HTTP 调用前提交；报告、FTS、请求终态和计划窗口在一个事务内提交。
/// </summary>
public sealed class FeedDigestExecutionStore(SqliteDatabase database)
    : IFeedDigestExecutionStore
{
    public async Task<FeedDigestExecutionBeginResult> BeginAsync(
        LocalScheduleRunLease lease,
        string reportId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        ValidateReportId(reportId);
        ValidateTimestamp(startedAtUtc, nameof(startedAtUtc));
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        LeaseState state = await ReadLeaseAsync(
                connection,
                transaction,
                lease,
                startedAtUtc,
                cancellationToken).ConfigureAwait(false)
            ?? throw StaleLease();
        if (!state.GenerationIsCurrent)
        {
            await CancelLeaseAsync(
                connection,
                transaction,
                lease,
                startedAtUtc,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return FeedDigestExecutionBeginResult
                .SuppressedUncertainPriorAttempt;
        }

        RequestState? existing = await ReadRequestAsync(
            connection,
            transaction,
            lease,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await InsertStartedAsync(
                connection,
                transaction,
                lease,
                reportId,
                startedAtUtc,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return FeedDigestExecutionBeginResult.Started;
        }

        if (existing.Status == "COMPLETED"
            && string.Equals(
                existing.ReportId,
                reportId,
                StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return FeedDigestExecutionBeginResult.AlreadyCompleted;
        }

        if (existing.Status == "STARTED")
        {
            await MarkRequestAsync(
                connection,
                transaction,
                lease,
                existing.ReportId,
                "AMBIGUOUS",
                startedAtUtc,
                requireAttempt: false,
                cancellationToken).ConfigureAwait(false);
        }
        await CancelLeaseAsync(
            connection,
            transaction,
            lease,
            startedAtUtc,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
        return FeedDigestExecutionBeginResult
            .SuppressedUncertainPriorAttempt;
    }

    public async Task ClearForSafeRetryAsync(
        LocalScheduleRunLease lease,
        string reportId,
        DateTimeOffset clearedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        ValidateReportId(reportId);
        ValidateTimestamp(clearedAtUtc, nameof(clearedAtUtc));
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        if (await ReadLeaseAsync(
                connection,
                transaction,
                lease,
                clearedAtUtc,
                cancellationToken).ConfigureAwait(false) is null)
        {
            throw StaleLease();
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM feed_digest_requests
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor
              AND report_id=$reportId
              AND attempt_count=$attemptCount
              AND status='STARTED';
            """;
        AddLeaseIdentity(command, lease);
        command.Parameters.AddWithValue("$reportId", reportId);
        command.Parameters.AddWithValue(
            "$attemptCount",
            lease.AttemptCount);
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "摘要模型调用状态不能安全释放重试。");
        }
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        LocalScheduleRunLease lease,
        AiReport report,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(report);
        ValidateReportId(report.Id);
        ValidateTimestamp(completedAtUtc, nameof(completedAtUtc));
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        LeaseState? state = await ReadLeaseAsync(
            connection,
            transaction,
            lease,
            completedAtUtc,
            cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            await MarkRequestAsync(
                connection,
                transaction,
                lease,
                report.Id,
                "AMBIGUOUS",
                completedAtUtc,
                requireAttempt: true,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return false;
        }
        if (!state.GenerationIsCurrent)
        {
            await MarkRequestAsync(
                connection,
                transaction,
                lease,
                report.Id,
                "DISCARDED",
                completedAtUtc,
                requireAttempt: true,
                cancellationToken).ConfigureAwait(false);
            await CancelLeaseAsync(
                connection,
                transaction,
                lease,
                completedAtUtc,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        await AiReportSql.UpsertAsync(
            connection,
            transaction,
            report,
            cancellationToken).ConfigureAwait(false);
        await MarkRequestAsync(
            connection,
            transaction,
            lease,
            report.Id,
            "COMPLETED",
            completedAtUtc,
            requireAttempt: true,
            cancellationToken).ConfigureAwait(false);
        await CompleteLeaseAsync(
            connection,
            transaction,
            lease,
            completedAtUtc,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public async Task AbandonUncertainAsync(
        LocalScheduleRunLease lease,
        string reportId,
        DateTimeOffset abandonedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        ValidateReportId(reportId);
        ValidateTimestamp(abandonedAtUtc, nameof(abandonedAtUtc));
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        await MarkRequestAsync(
            connection,
            transaction,
            lease,
            reportId,
            "AMBIGUOUS",
            abandonedAtUtc,
            requireAttempt: true,
            cancellationToken).ConfigureAwait(false);
        await TryCancelLeaseAsync(
            connection,
            transaction,
            lease,
            abandonedAtUtc,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<LeaseState?> ReadLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalScheduleRunLease lease,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN task.id IS NOT NULL
                              AND task.updated_at<=run.created_at
                        THEN 1 ELSE 0 END
            FROM local_schedule_runs AS run
            LEFT JOIN local_scheduled_tasks AS task
              ON task.id=run.schedule_id
            WHERE run.schedule_id=$scheduleId
              AND run.scheduled_for=$scheduledFor
              AND run.status='RUNNING'
              AND run.lease_token=$leaseToken
              AND run.lease_expires_at>$observedAt
              AND run.updated_at<=$observedAt;
            """;
        command.Parameters.AddWithValue(
            "$observedAt",
            Format(observedAtUtc));
        AddLeaseParameters(command, lease);
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is null ? null : new((long)value == 1);
    }

    private static async Task<RequestState?> ReadRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalScheduleRunLease lease,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT report_id, status
            FROM feed_digest_requests
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor;
            """;
        AddLeaseIdentity(command, lease);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task InsertStartedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalScheduleRunLease lease,
        string reportId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO feed_digest_requests(
                schedule_id, scheduled_for, report_id, attempt_count,
                status, created_at, updated_at, completed_at)
            VALUES(
                $scheduleId, $scheduledFor, $reportId, $attemptCount,
                'STARTED', $startedAt, $startedAt, NULL);
            """;
        AddLeaseIdentity(command, lease);
        command.Parameters.AddWithValue("$reportId", reportId);
        command.Parameters.AddWithValue(
            "$attemptCount",
            lease.AttemptCount);
        command.Parameters.AddWithValue(
            "$startedAt",
            Format(startedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "摘要模型调用状态无法持久化。");
        }
    }

    private static async Task MarkRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalScheduleRunLease lease,
        string reportId,
        string status,
        DateTimeOffset changedAtUtc,
        bool requireAttempt,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE feed_digest_requests
            SET status=$status,
                updated_at=$changedAt,
                completed_at=$changedAt
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor
              AND report_id=$reportId
              AND status='STARTED'
            """ + (requireAttempt
                ? " AND attempt_count=$attemptCount;"
                : ";");
        AddLeaseIdentity(command, lease);
        command.Parameters.AddWithValue("$reportId", reportId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue(
            "$changedAt",
            Format(changedAtUtc));
        if (requireAttempt)
        {
            command.Parameters.AddWithValue(
                "$attemptCount",
                lease.AttemptCount);
        }
        int changed = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (requireAttempt && changed != 1)
        {
            throw new InvalidOperationException(
                "摘要模型调用状态已由其他执行修改。");
        }
    }

    private static async Task CompleteLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalScheduleRunLease lease,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE local_schedule_runs
            SET status='COMPLETED',
                lease_token=NULL,
                lease_expires_at=NULL,
                updated_at=$completedAt,
                completed_at=$completedAt
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor
              AND status='RUNNING'
              AND lease_token=$leaseToken
              AND lease_expires_at>$completedAt
              AND updated_at<=$completedAt
              AND EXISTS(
                  SELECT 1
                  FROM local_scheduled_tasks AS task
                  WHERE task.id=local_schedule_runs.schedule_id
                    AND task.updated_at<=local_schedule_runs.created_at);
            """;
        command.Parameters.AddWithValue(
            "$completedAt",
            Format(completedAtUtc));
        AddLeaseParameters(command, lease);
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw StaleLease();
        }
    }

    private static Task CancelLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalScheduleRunLease lease,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken) =>
        ChangeLeaseToCancelledAsync(
            connection,
            transaction,
            lease,
            cancelledAtUtc,
            requireChange: true,
            cancellationToken);

    private static Task TryCancelLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalScheduleRunLease lease,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken) =>
        ChangeLeaseToCancelledAsync(
            connection,
            transaction,
            lease,
            cancelledAtUtc,
            requireChange: false,
            cancellationToken);

    private static async Task ChangeLeaseToCancelledAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalScheduleRunLease lease,
        DateTimeOffset cancelledAtUtc,
        bool requireChange,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE local_schedule_runs
            SET status='CANCELLED',
                lease_token=NULL,
                lease_expires_at=NULL,
                updated_at=$cancelledAt,
                completed_at=$cancelledAt
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor
              AND status='RUNNING'
              AND lease_token=$leaseToken
              AND lease_expires_at>$cancelledAt
              AND updated_at<=$cancelledAt;
            """;
        command.Parameters.AddWithValue(
            "$cancelledAt",
            Format(cancelledAtUtc));
        AddLeaseParameters(command, lease);
        int changed = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (requireChange && changed != 1)
        {
            throw StaleLease();
        }
    }

    private static void AddLeaseParameters(
        SqliteCommand command,
        LocalScheduleRunLease lease)
    {
        AddLeaseIdentity(command, lease);
        command.Parameters.AddWithValue("$leaseToken", lease.LeaseToken);
    }

    private static void AddLeaseIdentity(
        SqliteCommand command,
        LocalScheduleRunLease lease)
    {
        command.Parameters.AddWithValue("$scheduleId", lease.ScheduleId);
        command.Parameters.AddWithValue(
            "$scheduledFor",
            Format(lease.ScheduledForUtc));
    }

    private static void ValidateLease(LocalScheduleRunLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!Guid.TryParseExact(lease.ScheduleId, "D", out Guid scheduleId)
            || !string.Equals(
                scheduleId.ToString("D"),
                lease.ScheduleId,
                StringComparison.Ordinal)
            || !Guid.TryParseExact(lease.LeaseToken, "N", out Guid token)
            || !string.Equals(
                token.ToString("N"),
                lease.LeaseToken,
                StringComparison.Ordinal)
            || lease.AttemptCount < 1)
        {
            throw new ArgumentException(
                "摘要执行租约无效。",
                nameof(lease));
        }
        ValidateTimestamp(
            lease.ScheduledForUtc,
            nameof(lease.ScheduledForUtc));
    }

    private static void ValidateReportId(string reportId)
    {
        const string prefix = "feed-digest-";
        if (reportId.Length != prefix.Length + 64
            || !reportId.StartsWith(prefix, StringComparison.Ordinal)
            || reportId[prefix.Length..].Any(
                character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "摘要报告 ID 无效。",
                nameof(reportId));
        }
    }

    private static void ValidateTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "摘要执行时间戳必须是 UTC。",
                parameterName);
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static InvalidOperationException StaleLease() =>
        new("摘要执行租约已过期或计划代际已改变。");

    private sealed record LeaseState(bool GenerationIsCurrent);

    private sealed record RequestState(string ReportId, string Status);
}
