using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class LocalScheduledTaskRepository(SqliteDatabase database)
    : ILocalScheduledTaskRepository
{
    private const string TimeFormat = "HH:mm:ss.fffffff";
    private const string DateFormat = "yyyy-MM-dd";

    public async Task<LocalScheduledTask> SaveAsync(
        string id,
        LocalScheduleDefinition schedule,
        LocalScheduleMissedRunPolicy missedRunPolicy,
        bool isEnabled,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken,
        string? payload = null)
    {
        string validatedId = ValidateId(id);
        ValidateTimestamp(changedAtUtc, nameof(changedAtUtc));
        ValidatePolicy(missedRunPolicy);
        ValidatePayload(payload);
        DateTimeOffset? candidate = LocalScheduleCalculator
            .GetNextOccurrenceUtc(schedule, changedAtUtc);
        DateTimeOffset? nextRunAtUtc = isEnabled
            ? candidate ?? throw new ArgumentException(
                "启用计划必须存在晚于变更时间的执行时刻。",
                nameof(schedule))
            : null;

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        LocalScheduledTask? current = await ReadOneAsync(
            connection,
            transaction,
            validatedId,
            cancellationToken).ConfigureAwait(false);
        if (current is not null
            && changedAtUtc == current.UpdatedAtUtc
            && !string.Equals(
                payload,
                current.Payload,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "同一时间戳不能表示不同的本地计划负载。");
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_scheduled_tasks(
                id, frequency, time_zone_id, local_time,
                once_date, weekly_day, monthly_day, missed_run_policy,
                is_enabled, next_run_at, created_at, updated_at)
            VALUES(
                $id, $frequency, $timeZoneId, $localTime,
                $onceDate, $weeklyDay, $monthlyDay, $missedRunPolicy,
                $isEnabled, $nextRunAt, $changedAt, $changedAt)
            ON CONFLICT(id) DO UPDATE SET
                frequency=excluded.frequency,
                time_zone_id=excluded.time_zone_id,
                local_time=excluded.local_time,
                once_date=excluded.once_date,
                weekly_day=excluded.weekly_day,
                monthly_day=excluded.monthly_day,
                missed_run_policy=excluded.missed_run_policy,
                is_enabled=excluded.is_enabled,
                next_run_at=excluded.next_run_at,
                updated_at=excluded.updated_at
            WHERE local_scheduled_tasks.updated_at < excluded.updated_at
                OR (
                    local_scheduled_tasks.updated_at = excluded.updated_at
                    AND local_scheduled_tasks.frequency = excluded.frequency
                    AND local_scheduled_tasks.time_zone_id = excluded.time_zone_id
                    AND local_scheduled_tasks.local_time = excluded.local_time
                    AND local_scheduled_tasks.once_date IS excluded.once_date
                    AND local_scheduled_tasks.weekly_day IS excluded.weekly_day
                    AND local_scheduled_tasks.monthly_day IS excluded.monthly_day
                    AND local_scheduled_tasks.missed_run_policy = excluded.missed_run_policy
                    AND local_scheduled_tasks.is_enabled = excluded.is_enabled
                    AND local_scheduled_tasks.next_run_at IS excluded.next_run_at);
            """;
        AddScheduleParameters(
            command,
            validatedId,
            schedule,
            missedRunPolicy,
            isEnabled,
            nextRunAtUtc,
            changedAtUtc);
        int savedRows = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (savedRows != 1)
        {
            throw new InvalidOperationException(
                "较旧的本地计划变更不能覆盖新状态。");
        }
        await SavePayloadAsync(
            connection,
            transaction,
            validatedId,
            payload,
            cancellationToken).ConfigureAwait(false);
        await CancelUnownedInvalidatedRunsAsync(
            connection,
            transaction,
            validatedId,
            changedAtUtc,
            cancellationToken).ConfigureAwait(false);
        LocalScheduledTask saved =
            await ReadOneAsync(
                connection,
                transaction,
                validatedId,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "本地计划保存后无法读取。");
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
        return saved;
    }

    public async Task<LocalScheduledTask?> GetAsync(
        string id,
        CancellationToken cancellationToken)
    {
        string validatedId = ValidateId(id);
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        return await ReadOneAsync(
            connection,
            transaction: null,
            validatedId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LocalScheduledTask>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns + """

            ORDER BY task.created_at, task.id;
            """;
        var tasks = new List<LocalScheduledTask>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            tasks.Add(Read(reader));
        }
        return Array.AsReadOnly(tasks.ToArray());
    }

    public async Task<LocalScheduledTask?> SetEnabledAsync(
        string id,
        bool isEnabled,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        string validatedId = ValidateId(id);
        ValidateTimestamp(changedAtUtc, nameof(changedAtUtc));
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        LocalScheduledTask? current = await ReadOneAsync(
            connection,
            transaction,
            validatedId,
            cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        if (changedAtUtc < current.UpdatedAtUtc)
        {
            throw new InvalidOperationException(
                "较旧的本地计划变更不能覆盖新状态。");
        }
        if (changedAtUtc == current.UpdatedAtUtc)
        {
            if (isEnabled != current.IsEnabled)
            {
                throw new InvalidOperationException(
                    "同一时间戳不能表示不同的本地计划状态。");
            }
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return current;
        }

        DateTimeOffset? nextRunAtUtc = null;
        if (isEnabled)
        {
            nextRunAtUtc = LocalScheduleCalculator.GetNextOccurrenceUtc(
                current.Schedule,
                changedAtUtc)
                ?? throw new InvalidOperationException(
                    "已过期的单次计划不能重新启用。");
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE local_scheduled_tasks
            SET is_enabled=$isEnabled,
                next_run_at=$nextRunAt,
                updated_at=$updatedAt
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", validatedId);
        command.Parameters.AddWithValue("$isEnabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$nextRunAt",
            FormatNullable(nextRunAtUtc));
        command.Parameters.AddWithValue("$updatedAt", Format(changedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await CancelUnownedInvalidatedRunsAsync(
            connection,
            transaction,
            validatedId,
            changedAtUtc,
            cancellationToken).ConfigureAwait(false);
        LocalScheduledTask updated =
            await ReadOneAsync(
                connection,
                transaction,
                validatedId,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "本地计划启停后无法读取。");
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }

    private static async Task CancelUnownedInvalidatedRunsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scheduleId,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE local_schedule_runs
            SET status='CANCELLED',
                lease_token=NULL,
                lease_expires_at=NULL,
                updated_at=$changedAt,
                completed_at=$changedAt
            WHERE schedule_id=$scheduleId
              AND created_at<$changedAt
              AND updated_at<=$changedAt
              AND (status='PENDING'
                OR (status='RUNNING' AND lease_expires_at<=$changedAt));
            """;
        command.Parameters.AddWithValue("$scheduleId", scheduleId);
        command.Parameters.AddWithValue("$changedAt", Format(changedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        command.Parameters.Clear();
        command.CommandText = """
            DELETE FROM local_schedule_run_retries
            WHERE schedule_id=$scheduleId
              AND EXISTS(
                  SELECT 1
                  FROM local_schedule_runs AS run
                  WHERE run.schedule_id=local_schedule_run_retries.schedule_id
                    AND run.scheduled_for=local_schedule_run_retries.scheduled_for
                    AND run.status<>'PENDING');
            """;
        command.Parameters.AddWithValue("$scheduleId", scheduleId);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<LocalScheduledTask?> ReadOneAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + """

            WHERE task.id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    private static LocalScheduledTask Read(SqliteDataReader reader)
    {
        LocalScheduleFrequency frequency = ParseFrequency(reader.GetString(1));
        var definition = new LocalScheduleDefinition(
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
        bool isEnabled = reader.GetInt64(8) == 1;
        DateTimeOffset? nextRunAtUtc = ReadTimestamp(reader, 9);
        if (isEnabled != (nextRunAtUtc is not null))
        {
            throw new InvalidDataException(
                "本地计划启用状态与下一次执行时间不一致。");
        }
        return new(
            reader.GetString(0),
            definition,
            ParsePolicy(reader.GetString(7)),
            isEnabled,
            nextRunAtUtc,
            ReadRequiredTimestamp(reader, 10),
            ReadRequiredTimestamp(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private static void AddScheduleParameters(
        SqliteCommand command,
        string id,
        LocalScheduleDefinition schedule,
        LocalScheduleMissedRunPolicy missedRunPolicy,
        bool isEnabled,
        DateTimeOffset? nextRunAtUtc,
        DateTimeOffset changedAtUtc)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue(
            "$frequency",
            StoreFrequency(schedule.Frequency));
        command.Parameters.AddWithValue("$timeZoneId", schedule.TimeZoneId);
        command.Parameters.AddWithValue(
            "$localTime",
            schedule.LocalTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$onceDate",
            schedule.OnceDate is { } onceDate
                ? onceDate.ToString(DateFormat, CultureInfo.InvariantCulture)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$weeklyDay",
            schedule.WeeklyDay is { } weeklyDay
                ? (int)weeklyDay
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$monthlyDay",
            (object?)schedule.MonthlyDay ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$missedRunPolicy",
            StorePolicy(missedRunPolicy));
        command.Parameters.AddWithValue("$isEnabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$nextRunAt",
            FormatNullable(nextRunAtUtc));
        command.Parameters.AddWithValue("$changedAt", Format(changedAtUtc));
    }

    private static async Task SavePayloadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scheduleId,
        string? payload,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        if (payload is null)
        {
            command.CommandText = """
                DELETE FROM local_scheduled_task_payloads
                WHERE schedule_id=$scheduleId;
                """;
        }
        else
        {
            command.CommandText = """
                INSERT INTO local_scheduled_task_payloads(schedule_id, payload)
                VALUES($scheduleId, $payload)
                ON CONFLICT(schedule_id) DO UPDATE SET
                    payload=excluded.payload;
                """;
            command.Parameters.AddWithValue("$payload", payload);
        }
        command.Parameters.AddWithValue("$scheduleId", scheduleId);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "D", out Guid parsed)
            || !string.Equals(
                parsed.ToString("D"),
                id,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "本地计划 ID 必须是规范的小写 GUID。",
                nameof(id));
        }
        return id;
    }

    private static void ValidatePolicy(LocalScheduleMissedRunPolicy policy)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }

    private static void ValidatePayload(string? payload)
    {
        if (payload is not null
            && (payload.Length > 4_096 || payload.Any(char.IsControl)))
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }
    }

    private static void ValidateTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "本地计划时间戳必须是 UTC。",
                parameterName);
        }
    }

    private static string StoreFrequency(LocalScheduleFrequency frequency) =>
        frequency switch
        {
            LocalScheduleFrequency.Once => "ONCE",
            LocalScheduleFrequency.Daily => "DAILY",
            LocalScheduleFrequency.Weekly => "WEEKLY",
            LocalScheduleFrequency.Monthly => "MONTHLY",
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };

    private static LocalScheduleFrequency ParseFrequency(string value) =>
        value switch
        {
            "ONCE" => LocalScheduleFrequency.Once,
            "DAILY" => LocalScheduleFrequency.Daily,
            "WEEKLY" => LocalScheduleFrequency.Weekly,
            "MONTHLY" => LocalScheduleFrequency.Monthly,
            _ => throw new InvalidDataException("本地计划频率无效。")
        };

    private static string StorePolicy(LocalScheduleMissedRunPolicy policy) =>
        policy switch
        {
            LocalScheduleMissedRunPolicy.RunOnce => "RUN_ONCE",
            LocalScheduleMissedRunPolicy.Skip => "SKIP",
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };

    private static LocalScheduleMissedRunPolicy ParsePolicy(string value) =>
        value switch
        {
            "RUN_ONCE" => LocalScheduleMissedRunPolicy.RunOnce,
            "SKIP" => LocalScheduleMissedRunPolicy.Skip,
            _ => throw new InvalidDataException("本地计划错过执行策略无效。")
        };

    private static DateOnly? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateOnly.ParseExact(
                reader.GetString(ordinal),
                DateFormat,
                CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadRequiredTimestamp(
        SqliteDataReader reader,
        int ordinal) =>
        ReadTimestamp(reader, ordinal)
        ?? throw new InvalidDataException("本地计划时间戳缺失。");

    private static DateTimeOffset? ReadTimestamp(
        SqliteDataReader reader,
        int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        DateTimeOffset value = DateTimeOffset.ParseExact(
            reader.GetString(ordinal),
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "本地计划存储的时间戳不是 UTC。");
        }
        return value;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullable(DateTimeOffset? value) =>
        value is null ? DBNull.Value : Format(value.Value);

    private const string SelectColumns = """
        SELECT task.id, task.frequency, task.time_zone_id, task.local_time,
               task.once_date, task.weekly_day, task.monthly_day,
               task.missed_run_policy, task.is_enabled, task.next_run_at,
               task.created_at, task.updated_at, payload.payload
        FROM local_scheduled_tasks AS task
        LEFT JOIN local_scheduled_task_payloads AS payload
          ON payload.schedule_id=task.id
        """;
}
