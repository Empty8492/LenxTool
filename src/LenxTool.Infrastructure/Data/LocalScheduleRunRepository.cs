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

    public Task<LocalScheduleRunLease?> ClaimDueAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset missedBeforeUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        ClaimDueCoreAsync(
            eligibleScheduleIds: null,
            nowUtc,
            missedBeforeUtc,
            leaseDuration,
            cancellationToken);

    public Task<LocalScheduleRunLease?> ClaimDueAsync(
        IReadOnlyCollection<string> eligibleScheduleIds,
        DateTimeOffset nowUtc,
        DateTimeOffset missedBeforeUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibleScheduleIds);
        return ClaimDueCoreAsync(
            ValidateEligibleScheduleIds(eligibleScheduleIds),
            nowUtc,
            missedBeforeUtc,
            leaseDuration,
            cancellationToken);
    }

    private async Task<LocalScheduleRunLease?> ClaimDueCoreAsync(
        IReadOnlyList<string>? eligibleScheduleIds,
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
        if (eligibleScheduleIds is { Count: 0 })
        {
            return null;
        }

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        await CancelInvalidatedUnownedRunsAsync(
            connection,
            transaction,
            eligibleScheduleIds,
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        for (int scan = 0;
             scan < MaximumSkippedSchedulesPerClaim;
             scan++)
        {
            ExistingRunCandidate? existing = await ReadClaimableRunAsync(
                connection,
                transaction,
                eligibleScheduleIds,
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
                eligibleScheduleIds,
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

    public async Task<bool> IsCancellationRequestedAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        ValidateTimestamp(observedAtUtc, nameof(observedAtUtc));
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
                WHEN task.id IS NULL
                  OR task.updated_at>run.created_at
                THEN 1
                ELSE 0
            END
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
        if (value is null)
        {
            throw StaleLease();
        }
        return (long)value == 1;
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
            completedAtUtc,
            retryNotBeforeUtc: null,
            isTerminal: true,
            allowScheduleMutation: false,
            cancellationToken);

    public Task CancelAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken) =>
        FinishLeaseAsync(
            lease,
            "CANCELLED",
            cancelledAtUtc,
            cancelledAtUtc,
            retryNotBeforeUtc: null,
            isTerminal: true,
            allowScheduleMutation: true,
            cancellationToken);

    public Task ReleaseAsync(
        LocalScheduleRunLease lease,
        DateTimeOffset releasedAtUtc,
        CancellationToken cancellationToken,
        DateTimeOffset? retryNotBeforeUtc = null) =>
        FinishLeaseAsync(
            lease,
            "PENDING",
            releasedAtUtc,
            releasedAtUtc,
            retryNotBeforeUtc,
            isTerminal: false,
            allowScheduleMutation: false,
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

            WHERE run.schedule_id=$scheduleId
            ORDER BY run.scheduled_for DESC
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

    private static async Task CancelInvalidatedUnownedRunsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string>? eligibleScheduleIds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE local_schedule_runs
            SET status='CANCELLED',
                lease_token=NULL,
                lease_expires_at=NULL,
                updated_at=$now,
                completed_at=$now
            WHERE ((status='PENDING' AND updated_at<=$now)
                OR (status='RUNNING' AND lease_expires_at<=$now))
              AND NOT EXISTS(
                  SELECT 1
                  FROM local_scheduled_tasks AS task
                  WHERE task.id=local_schedule_runs.schedule_id
                    AND task.updated_at<=local_schedule_runs.created_at)
            """ + BuildEligiblePredicate(
                command,
                "local_schedule_runs.schedule_id",
                eligibleScheduleIds) + ";";
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await DeleteRetriesForTerminalRunsAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExistingRunCandidate?> ReadClaimableRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string>? eligibleScheduleIds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run.schedule_id, run.scheduled_for, run.attempt_count
            FROM local_schedule_runs AS run
            LEFT JOIN local_schedule_run_retries AS retry
              ON retry.schedule_id=run.schedule_id
             AND retry.scheduled_for=run.scheduled_for
            WHERE ((run.status='PENDING'
                    AND COALESCE(
                        retry.retry_not_before,
                        run.updated_at)<=$now)
               OR (run.status='RUNNING' AND run.lease_expires_at<=$now))
            """ + BuildEligiblePredicate(
                command,
                "run.schedule_id",
                eligibleScheduleIds) + """

            ORDER BY run.scheduled_for, run.schedule_id
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
              AND ((status='PENDING'
                    AND COALESCE(
                        (SELECT retry.retry_not_before
                         FROM local_schedule_run_retries AS retry
                         WHERE retry.schedule_id=local_schedule_runs.schedule_id
                           AND retry.scheduled_for=local_schedule_runs.scheduled_for),
                        updated_at)<=$now)
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
        if (changed == 1)
        {
            await DeleteRetryAsync(
                connection,
                transaction,
                candidate.ScheduleId,
                candidate.ScheduledForUtc,
                cancellationToken).ConfigureAwait(false);
        }
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
        IReadOnlyList<string>? eligibleScheduleIds,
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
            """ + BuildEligiblePredicate(
                command,
                "task.id",
                eligibleScheduleIds) + """

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
        DateTimeOffset observedAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? retryNotBeforeUtc,
        bool isTerminal,
        bool allowScheduleMutation,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        ValidateTimestamp(observedAtUtc, nameof(observedAtUtc));
        ValidateTimestamp(updatedAtUtc, nameof(updatedAtUtc));
        if (retryNotBeforeUtc is { } retryAt)
        {
            ValidateTimestamp(retryAt, nameof(retryNotBeforeUtc));
            if (isTerminal
                || retryAt < observedAtUtc
                || retryAt - observedAtUtc > TimeSpan.FromDays(1))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retryNotBeforeUtc));
            }
        }
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
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
              AND lease_expires_at>$observedAt
              AND updated_at<=$observedAt
              AND ($allowScheduleMutation=1 OR EXISTS(
                  SELECT 1
                  FROM local_scheduled_tasks AS task
                  WHERE task.id=local_schedule_runs.schedule_id
                    AND task.updated_at<=local_schedule_runs.created_at));
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue(
            "$observedAt",
            Format(observedAtUtc));
        command.Parameters.AddWithValue("$updatedAt", Format(updatedAtUtc));
        command.Parameters.AddWithValue(
            "$completedAt",
            isTerminal ? Format(updatedAtUtc) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$allowScheduleMutation",
            allowScheduleMutation ? 1 : 0);
        AddLeaseParameters(command, lease);
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw StaleLease();
        }
        if (!isTerminal && retryNotBeforeUtc is { } scheduledRetryAt)
        {
            await SaveRetryAsync(
                connection,
                transaction,
                lease.ScheduleId,
                lease.ScheduledForUtc,
                scheduledRetryAt,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DeleteRetryAsync(
                connection,
                transaction,
                lease.ScheduleId,
                lease.ScheduledForUtc,
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
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
                : ParseTimestamp(reader.GetString(6)),
            reader.IsDBNull(7)
                ? null
                : ParseTimestamp(reader.GetString(7)));

    private static async Task SaveRetryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string scheduleId,
        DateTimeOffset scheduledForUtc,
        DateTimeOffset retryNotBeforeUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_schedule_run_retries(
                schedule_id, scheduled_for, retry_not_before)
            VALUES($scheduleId, $scheduledFor, $retryNotBefore)
            ON CONFLICT(schedule_id, scheduled_for) DO UPDATE SET
                retry_not_before=excluded.retry_not_before;
            """;
        command.Parameters.AddWithValue("$scheduleId", scheduleId);
        command.Parameters.AddWithValue(
            "$scheduledFor",
            Format(scheduledForUtc));
        command.Parameters.AddWithValue(
            "$retryNotBefore",
            Format(retryNotBeforeUtc));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DeleteRetryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string scheduleId,
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM local_schedule_run_retries
            WHERE schedule_id=$scheduleId
              AND scheduled_for=$scheduledFor;
            """;
        command.Parameters.AddWithValue("$scheduleId", scheduleId);
        command.Parameters.AddWithValue(
            "$scheduledFor",
            Format(scheduledForUtc));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DeleteRetriesForTerminalRunsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM local_schedule_run_retries
            WHERE EXISTS(
                SELECT 1
                FROM local_schedule_runs AS run
                WHERE run.schedule_id=local_schedule_run_retries.schedule_id
                  AND run.scheduled_for=local_schedule_run_retries.scheduled_for
                  AND run.status<>'PENDING');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

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

    private static string BuildEligiblePredicate(
        SqliteCommand command,
        string columnName,
        IReadOnlyList<string>? eligibleScheduleIds)
    {
        if (eligibleScheduleIds is null)
        {
            return string.Empty;
        }

        var parameterNames = new string[eligibleScheduleIds.Count];
        for (int index = 0; index < eligibleScheduleIds.Count; index++)
        {
            string parameterName = $"$eligibleSchedule{index}";
            parameterNames[index] = parameterName;
            command.Parameters.AddWithValue(
                parameterName,
                eligibleScheduleIds[index]);
        }
        return $" AND {columnName} IN ({string.Join(", ", parameterNames)})";
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

    private static string[] ValidateEligibleScheduleIds(
        IReadOnlyCollection<string> scheduleIds)
    {
        if (scheduleIds.Count > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(scheduleIds));
        }

        return scheduleIds
            .Select(ValidateScheduleId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
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
        SELECT run.schedule_id, run.scheduled_for, run.status,
               run.attempt_count, run.created_at, run.updated_at,
               run.completed_at, retry.retry_not_before
        FROM local_schedule_runs AS run
        LEFT JOIN local_schedule_run_retries AS retry
          ON retry.schedule_id=run.schedule_id
         AND retry.scheduled_for=run.scheduled_for
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
