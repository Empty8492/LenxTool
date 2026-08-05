using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class LocalScheduleRunRepository(SqliteDatabase database)
    : ILocalScheduleRunRepository
{
    private const int MaximumSkippedSchedulesPerClaim = 200;
    private const string TimeFormat = "HH:mm:ss.fffffff";
    private const string DateFormat = "yyyy-MM-dd";

    public async Task<LocalScheduleRunLease?> ClaimDueAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset missedBeforeUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateTimestamp(nowUtc, nameof(nowUtc));
        ValidateTimestamp(missedBeforeUtc, nameof(missedBeforeUtc));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            missedBeforeUtc,
            nowUtc);
        ValidateLeaseDuration(leaseDuration);

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);

        for (int scan = 0;
             scan < MaximumSkippedSchedulesPerClaim;
             scan++)
        {
            ExistingRunCandidate? existing = await ReadClaimableRunAsync(
                connection,
                transaction,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                LocalScheduleRunLease? reclaimed = await ClaimExistingRunAsync(
                    connection,
                    transaction,
                    existing,
                    nowUtc,
                    leaseDuration,
                    cancellationToken).ConfigureAwait(false);
                if (reclaimed is not null)
                {
                    await transaction.CommitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return reclaimed;
                }
                continue;
            }

            ScheduledTaskCandidate? candidate = await ReadDueScheduleAsync(
                connection,
                transaction,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            if (candidate is null)
            {
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            DateTimeOffset? nextRunAtUtc =
                LocalScheduleCalculator.GetNextOccurrenceUtc(
                    candidate.Schedule,
                    nowUtc);
            await AdvanceScheduleAsync(
                connection,
                transaction,
                candidate,
                nextRunAtUtc,
                nowUtc,
                cancellationToken).ConfigureAwait(false);

            bool missed = candidate.ScheduledForUtc < missedBeforeUtc;
            if (missed
                && candidate.MissedRunPolicy
                    == LocalScheduleMissedRunPolicy.Skip)
            {
                // Skip 只推进游标，不制造一条看似执行过的历史记录。
                continue;
            }

            LocalScheduleRunLease created = await InsertClaimedRunAsync(
                connection,
                transaction,
                candidate,
                nowUtc,
                leaseDuration,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return created;
        }

        // 一次调用最多推进有限数量的 Skip 计划，防止异常积压长期占住写事务；
        // 后续轮询会从新的最早游标继续收敛。
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
        return null;
    }

    public async Task<bool> RenewLeaseAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        ValidateTimestamp(renewedAtUtc, nameof(renewedAtUtc));
        ValidateTimestamp(leaseExpiresAtUtc, nameof(leaseExpiresAtUtc));
        if (leaseExpiresAtUtc <= renewedAtUtc
            || leaseExpiresAtUtc - renewedAtUtc > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc));
        }

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE local_schedule_runs
            SET lease_expires_at=$leaseExpiresAt,
                updated_at=$renewedAt
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor
              AND status='RUNNING'
              AND lease_token=$leaseToken
              AND lease_expires_at>$renewedAt
              AND ((updated_at<$renewedAt
                    AND lease_expires_at<=$leaseExpiresAt)
                OR (updated_at=$renewedAt
                    AND lease_expires_at=$leaseExpiresAt));
            """;
        command.Parameters.AddWithValue(
            "$leaseExpiresAt",
            Format(leaseExpiresAtUtc));
        command.Parameters.AddWithValue("$renewedAt", Format(renewedAtUtc));
        AddLeaseParameters(command, lease);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public Task CompleteAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) =>
        FinishLeaseAsync(
            lease,
            "COMPLETED",
            completedAtUtc,
            isTerminal: true,
            cancellationToken);

    public Task CancelAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken) =>
        FinishLeaseAsync(
            lease,
            "CANCELLED",
            cancelledAtUtc,
            isTerminal: true,
            cancellationToken);

    public Task ReleaseAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset releasedAtUtc,
        CancellationToken cancellationToken) =>
        FinishLeaseAsync(
            lease,
            "PENDING",
            releasedAtUtc,
            isTerminal: false,
            cancellationToken);

    public async Task<IReadOnlyList<LocalScheduleRun>> GetRecentAsync(
        string scheduleId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        string validatedId = ValidateScheduleId(scheduleId);
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectRunColumns + """

            WHERE schedule_id=$scheduleId
            ORDER BY scheduled_for DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$scheduleId", validatedId);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var runs = new List<LocalScheduleRun>(maximumCount);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            runs.Add(ReadRun(reader));
        }
        return Array.AsReadOnly(runs.ToArray());
    }

    private static async Task<ExistingRunCandidate?> ReadClaimableRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT schedule_id, scheduled_for, attempt_count
            FROM local_schedule_runs
            WHERE (status='PENDING' AND updated_at<=$now)
               OR (status='RUNNING' AND lease_expires_at<=$now)
            ORDER BY scheduled_for, schedule_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }
        return new(
            reader.GetString(0),
            ParseTimestamp(reader.GetString(1)),
            reader.GetInt32(2));
    }

    private static async Task<LocalScheduleRunLease?> ClaimExistingRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExistingRunCandidate candidate,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        string leaseToken = Guid.NewGuid().ToString("N");
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE local_schedule_runs
            SET status='RUNNING',
                attempt_count=attempt_count+1,
                lease_token=$leaseToken,
                lease_expires_at=$leaseExpiresAt,
                updated_at=$now
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor
              AND ((status='PENDING' AND updated_at<=$now)
                OR (status='RUNNING' AND lease_expires_at<=$now));
            """;
        command.Parameters.AddWithValue("$leaseToken", leaseToken);
        command.Parameters.AddWithValue(
            "$leaseExpiresAt",
            Format(nowUtc.Add(leaseDuration)));
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        command.Parameters.AddWithValue("$scheduleId", candidate.ScheduleId);
        command.Parameters.AddWithValue(
            "$scheduledFor",
            Format(candidate.ScheduledForUtc));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        return changed == 1
            ? new(
                candidate.ScheduleId,
                candidate.ScheduledForUtc,
                checked(candidate.AttemptCount + 1),
                leaseToken)
            : null;
    }

    private static async Task<ScheduledTaskCandidate?> ReadDueScheduleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT task.id, task.frequency, task.time_zone_id,
                   task.local_time, task.once_date, task.weekly_day,
                   task.monthly_day, task.missed_run_policy,
                   task.next_run_at
            FROM local_scheduled_tasks AS task
            WHERE task.is_enabled=1
              AND task.next_run_at<=$now
              AND NOT EXISTS(
                  SELECT 1
                  FROM local_schedule_runs AS run
                  WHERE run.schedule_id=task.id
                    AND run.status IN ('PENDING', 'RUNNING'))
            ORDER BY task.next_run_at, task.id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        LocalScheduleFrequency frequency = ParseFrequency(reader.GetString(1));
        var schedule = new LocalScheduleDefinition(
            frequency,
            reader.GetString(2),
            TimeOnly.ParseExact(
                reader.GetString(3),
                TimeFormat,
                CultureInfo.InvariantCulture),
            ReadDate(reader, 4),
            reader.IsDBNull(5)
                ? null
                : (DayOfWeek)reader.GetInt32(5),
            reader.IsDBNull(6)
                ? null
                : reader.GetInt32(6));
        return new(
            reader.GetString(0),
            schedule,
            ParsePolicy(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)));
    }

    private static async Task AdvanceScheduleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledTaskCandidate candidate,
        DateTimeOffset? nextRunAtUtc,
        DateTimeOffset advancedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE local_scheduled_tasks
            SET is_enabled=$isEnabled,
                next_run_at=$nextRunAt,
                updated_at=$advancedAt
            WHERE id=$scheduleId
              AND is_enabled=1
              AND next_run_at=$scheduledFor;
            """;
        command.Parameters.AddWithValue(
            "$isEnabled",
            nextRunAtUtc is null ? 0 : 1);
        command.Parameters.AddWithValue(
            "$nextRunAt",
            nextRunAtUtc is null
                ? DBNull.Value
                : Format(nextRunAtUtc.Value));
        command.Parameters.AddWithValue("$advancedAt", Format(advancedAtUtc));
        command.Parameters.AddWithValue("$scheduleId", candidate.ScheduleId);
        command.Parameters.AddWithValue(
            "$scheduledFor",
            Format(candidate.ScheduledForUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "本地计划游标已被其他写入修改，无法领取当前窗口。");
        }
    }

    private static async Task<LocalScheduleRunLease> InsertClaimedRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScheduledTaskCandidate candidate,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        string leaseToken = Guid.NewGuid().ToString("N");
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_schedule_runs(
                schedule_id, scheduled_for, status, attempt_count,
                lease_token, lease_expires_at, created_at, updated_at,
                completed_at)
            VALUES(
                $scheduleId, $scheduledFor, 'RUNNING', 1,
                $leaseToken, $leaseExpiresAt, $claimedAt, $claimedAt,
                NULL);
            """;
        command.Parameters.AddWithValue("$scheduleId", candidate.ScheduleId);
        command.Parameters.AddWithValue(
            "$scheduledFor",
            Format(candidate.ScheduledForUtc));
        command.Parameters.AddWithValue("$leaseToken", leaseToken);
        command.Parameters.AddWithValue(
            "$leaseExpiresAt",
            Format(claimedAtUtc.Add(leaseDuration)));
        command.Parameters.AddWithValue("$claimedAt", Format(claimedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "本地计划窗口无法持久化。");
        }
        return new(
            candidate.ScheduleId,
            candidate.ScheduledForUtc,
            AttemptCount: 1,
            leaseToken);
    }

    private async Task FinishLeaseAsync(
        LocalScheduleRunLease lease,
        string status,
        DateTimeOffset updatedAtUtc,
        bool isTerminal,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        ValidateTimestamp(updatedAtUtc, nameof(updatedAtUtc));
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE local_schedule_runs
            SET status=$status,
                lease_token=NULL,
                lease_expires_at=NULL,
                updated_at=$updatedAt,
                completed_at=$completedAt
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor
              AND status='RUNNING'
              AND lease_token=$leaseToken
              AND lease_expires_at>$updatedAt
              AND updated_at<=$updatedAt;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updatedAt", Format(updatedAtUtc));
        command.Parameters.AddWithValue(
            "$completedAt",
            isTerminal ? Format(updatedAtUtc) : DBNull.Value);
        AddLeaseParameters(command, lease);
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw StaleLease();
        }
    }

    private static LocalScheduleRun ReadRun(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            ParseTimestamp(reader.GetString(1)),
            ParseStatus(reader.GetString(2)),
            reader.GetInt32(3),
            ParseTimestamp(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)),
            reader.IsDBNull(6)
                ? null
                : ParseTimestamp(reader.GetString(6)));

    private static void AddLeaseParameters(
        SqliteCommand command,
        LocalScheduleRunLease lease)
    {
        command.Parameters.AddWithValue("$scheduleId", lease.ScheduleId);
        command.Parameters.AddWithValue(
            "$scheduledFor",
            Format(lease.ScheduledForUtc));
        command.Parameters.AddWithValue("$leaseToken", lease.LeaseToken);
    }

    private static void ValidateLease(LocalScheduleRunLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateScheduleId(lease.ScheduleId);
        ValidateTimestamp(
            lease.ScheduledForUtc,
            nameof(lease.ScheduledForUtc));
        if (!Guid.TryParseExact(lease.LeaseToken, "N", out Guid token)
            || !string.Equals(
                token.ToString("N"),
                lease.LeaseToken,
                StringComparison.Ordinal)
            || lease.AttemptCount < 1)
        {
            throw new InvalidDataException(
                "本地计划窗口租约元数据无效。");
        }
    }

    private static string ValidateScheduleId(string scheduleId)
    {
        if (!Guid.TryParseExact(scheduleId, "D", out Guid parsed)
            || !string.Equals(
                parsed.ToString("D"),
                scheduleId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "本地计划 ID 必须是规范的小写 GUID。",
                nameof(scheduleId));
        }
        return scheduleId;
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero
            || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    private static void ValidateTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "本地计划窗口时间戳必须是 UTC。",
                parameterName);
        }
    }

    private static LocalScheduleFrequency ParseFrequency(string value) =>
        value switch
        {
            "ONCE" => LocalScheduleFrequency.Once,
            "DAILY" => LocalScheduleFrequency.Daily,
            "WEEKLY" => LocalScheduleFrequency.Weekly,
            "MONTHLY" => LocalScheduleFrequency.Monthly,
            _ => throw new InvalidDataException("本地计划频率无效。")
        };

    private static LocalScheduleMissedRunPolicy ParsePolicy(string value) =>
        value switch
        {
            "RUN_ONCE" => LocalScheduleMissedRunPolicy.RunOnce,
            "SKIP" => LocalScheduleMissedRunPolicy.Skip,
            _ => throw new InvalidDataException("本地计划错过执行策略无效。")
        };

    private static LocalScheduleRunStatus ParseStatus(string value) =>
        value switch
        {
            "PENDING" => LocalScheduleRunStatus.Pending,
            "RUNNING" => LocalScheduleRunStatus.Running,
            "COMPLETED" => LocalScheduleRunStatus.Completed,
            "CANCELLED" => LocalScheduleRunStatus.Cancelled,
            _ => throw new InvalidDataException("本地计划窗口状态无效。")
        };

    private static DateOnly? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateOnly.ParseExact(
                reader.GetString(ordinal),
                DateFormat,
                CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        DateTimeOffset timestamp = DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "本地计划窗口存储的时间戳不是 UTC。");
        }
        return timestamp;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private const string SelectRunColumns = """
        SELECT schedule_id, scheduled_for, status, attempt_count,
               created_at, updated_at, completed_at
        FROM local_schedule_runs
        """;

    private static InvalidOperationException StaleLease() =>
        new("本地计划窗口租约已过期或已由其他处理器接管。");

    private sealed record ExistingRunCandidate(
        string ScheduleId,
        DateTimeOffset ScheduledForUtc,
        int AttemptCount);

    private sealed record ScheduledTaskCandidate(
        string ScheduleId,
        LocalScheduleDefinition Schedule,
        LocalScheduleMissedRunPolicy MissedRunPolicy,
        DateTimeOffset ScheduledForUtc);
}
