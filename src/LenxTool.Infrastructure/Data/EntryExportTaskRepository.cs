using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class EntryExportTaskRepository(SqliteDatabase database)
    : IEntryExportTaskRepository
{
    public async Task<EntryExportEnqueueResult> EnqueueAsync(
        EntryExportRequest request,
        DateTimeOffset enqueuedAt,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        string timestamp = Format(enqueuedAt);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO entry_export_tasks(
                idempotency_key, exporter_id, target_id, entry_id, content_hash,
                view_kind, content_bytes, status, attempt_count, next_attempt_at,
                lease_token, lease_expires_at, cancellation_requested,
                last_error_code, created_at, updated_at, completed_at)
            VALUES(
                $idempotencyKey, $exporterId, $targetId, $entryId, $contentHash,
                $viewKind, $contentBytes, 'QUEUED', 0, $nextAttemptAt,
                NULL, NULL, 0, NULL, $createdAt, $updatedAt, NULL);
            """;
        AddRequestParameters(command, request);
        command.Parameters.AddWithValue("$nextAttemptAt", timestamp);
        command.Parameters.AddWithValue("$createdAt", timestamp);
        command.Parameters.AddWithValue("$updatedAt", timestamp);
        bool created = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;

        command.Parameters.Clear();
        command.CommandText = SelectColumnsSql + """

            WHERE idempotency_key=$idempotencyKey;
            """;
        command.Parameters.AddWithValue(
            "$idempotencyKey",
            request.IdempotencyKey);
        EntryExportTask task;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(
                         cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The export task could not be read after enqueue.");
            }
            task = ReadTask(reader);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(task, created);
    }

    public async Task<EntryExportTaskLease?> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero
            || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        string timestamp = Format(now);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // 若应用在收到取消后退出，过期租约会在下次领取前收敛为终态，
        // 防止“取消请求”因进程重启再次被执行。
        command.CommandText = """
            UPDATE entry_export_tasks
            SET status='CANCELLED',
                next_attempt_at=NULL,
                lease_token=NULL,
                lease_expires_at=NULL,
                cancellation_requested=0,
                last_error_code=NULL,
                completed_at=$now,
                updated_at=$now
            WHERE status='RUNNING'
              AND cancellation_requested=1
              AND lease_expires_at<=$now;
            """;
        command.Parameters.AddWithValue("$now", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = """
            SELECT idempotency_key, exporter_id, target_id, entry_id,
                   content_hash, view_kind, content_bytes, attempt_count
            FROM entry_export_tasks
            WHERE (status='QUEUED' AND next_attempt_at<=$now)
               OR (status='RUNNING'
                   AND cancellation_requested=0
                   AND lease_expires_at<=$now)
            ORDER BY created_at, idempotency_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", timestamp);
        ExportCandidate? candidate = null;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(
                         cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidate = new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    ParseViewKind(reader.GetString(5)),
                    reader.GetInt64(6),
                    reader.GetInt32(7));
            }
        }

        EntryExportTaskLease? claimed = null;
        if (candidate is not null)
        {
            string leaseToken = Guid.NewGuid().ToString();
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE entry_export_tasks
                SET status='RUNNING',
                    attempt_count=attempt_count+1,
                    next_attempt_at=NULL,
                    lease_token=$leaseToken,
                    lease_expires_at=$leaseExpiresAt,
                    cancellation_requested=0,
                    updated_at=$now
                WHERE idempotency_key=$idempotencyKey
                  AND ((status='QUEUED' AND next_attempt_at<=$now)
                    OR (status='RUNNING'
                        AND cancellation_requested=0
                        AND lease_expires_at<=$now));
                """;
            command.Parameters.AddWithValue("$leaseToken", leaseToken);
            command.Parameters.AddWithValue(
                "$leaseExpiresAt",
                Format(now.Add(leaseDuration)));
            command.Parameters.AddWithValue("$now", timestamp);
            command.Parameters.AddWithValue(
                "$idempotencyKey",
                candidate.IdempotencyKey);
            int changed = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (changed == 1)
            {
                claimed = new(
                    candidate.IdempotencyKey,
                    candidate.ExporterId,
                    candidate.TargetId,
                    candidate.EntryId,
                    candidate.ContentHash,
                    candidate.ViewKind,
                    candidate.ContentBytes,
                    checked(candidate.AttemptCount + 1),
                    leaseToken);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async Task<bool> IsCancellationRequestedAsync(
        EntryExportTaskLease task,
        CancellationToken cancellationToken)
    {
        ValidateLease(task);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT cancellation_requested
            FROM entry_export_tasks
            WHERE idempotency_key=$idempotencyKey
              AND status='RUNNING'
              AND lease_token=$leaseToken;
            """;
        AddLeaseParameters(command, task);
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (value is null)
        {
            throw StaleLease();
        }
        return (long)value == 1;
    }

    public async Task<bool> RenewLeaseAsync(
        EntryExportTaskLease task,
        DateTimeOffset renewedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        ValidateLease(task);
        if (leaseExpiresAt <= renewedAt
            || leaseExpiresAt - renewedAt > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        }
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE entry_export_tasks
            SET lease_expires_at=$leaseExpiresAt,
                updated_at=$renewedAt
            WHERE idempotency_key=$idempotencyKey
              AND status='RUNNING'
              AND lease_token=$leaseToken;
            """;
        command.Parameters.AddWithValue(
            "$leaseExpiresAt",
            Format(leaseExpiresAt));
        command.Parameters.AddWithValue("$renewedAt", Format(renewedAt));
        AddLeaseParameters(command, task);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public Task CompleteAsync(
        EntryExportTaskLease task,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        return FinishLeaseAsync(
            task,
            status: "COMPLETED",
            errorCode: null,
            nextAttemptAt: null,
            completedAt,
            completedAt,
            // 适配器已返回成功后副作用不可撤销，成功提交优先于迟到的取消请求。
            allowCancellationRequest: true,
            cancellationToken);
    }

    public Task FailAsync(
        EntryExportTaskLease task,
        EntryExportTaskErrorCode errorCode,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        ValidateErrorCode(errorCode);
        return FinishLeaseAsync(
            task,
            status: "FAILED",
            errorCode,
            nextAttemptAt: null,
            completedAt: failedAt,
            updatedAt: failedAt,
            allowCancellationRequest: false,
            cancellationToken);
    }

    public Task ScheduleRetryAsync(
        EntryExportTaskLease task,
        EntryExportTaskErrorCode errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        ValidateErrorCode(errorCode);
        ArgumentOutOfRangeException.ThrowIfLessThan(nextAttemptAt, failedAt);
        return FinishLeaseAsync(
            task,
            status: "QUEUED",
            errorCode,
            nextAttemptAt,
            completedAt: null,
            updatedAt: failedAt,
            allowCancellationRequest: false,
            cancellationToken);
    }

    public Task CancelClaimedAsync(
        EntryExportTaskLease task,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken) =>
        FinishLeaseAsync(
            task,
            status: "CANCELLED",
            errorCode: null,
            nextAttemptAt: null,
            completedAt: cancelledAt,
            updatedAt: cancelledAt,
            allowCancellationRequest: true,
            cancellationToken);

    public Task ReleaseAsync(
        EntryExportTaskLease task,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken) =>
        FinishLeaseAsync(
            task,
            status: "QUEUED",
            errorCode: null,
            nextAttemptAt: releasedAt,
            completedAt: null,
            updatedAt: releasedAt,
            allowCancellationRequest: false,
            cancellationToken);

    public async Task<EntryExportCancellationResult> RequestCancellationAsync(
        string idempotencyKey,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        string timestamp = Format(requestedAt);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status
            FROM entry_export_tasks
            WHERE idempotency_key=$idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        string? status = (string?)await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        EntryExportCancellationResult result;
        switch (status)
        {
            case null:
                result = EntryExportCancellationResult.NotFound;
                break;
            case "QUEUED":
                command.Parameters.Clear();
                command.CommandText = """
                    UPDATE entry_export_tasks
                    SET status='CANCELLED',
                        next_attempt_at=NULL,
                        last_error_code=NULL,
                        completed_at=$requestedAt,
                        updated_at=$requestedAt
                    WHERE idempotency_key=$idempotencyKey
                      AND status='QUEUED';
                    """;
                command.Parameters.AddWithValue("$requestedAt", timestamp);
                command.Parameters.AddWithValue(
                    "$idempotencyKey",
                    idempotencyKey);
                result = await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) == 1
                    ? EntryExportCancellationResult.Cancelled
                    : EntryExportCancellationResult.AlreadyTerminal;
                break;
            case "RUNNING":
                command.Parameters.Clear();
                command.CommandText = """
                    UPDATE entry_export_tasks
                    SET cancellation_requested=1,
                        updated_at=$requestedAt
                    WHERE idempotency_key=$idempotencyKey
                      AND status='RUNNING';
                    """;
                command.Parameters.AddWithValue("$requestedAt", timestamp);
                command.Parameters.AddWithValue(
                    "$idempotencyKey",
                    idempotencyKey);
                result = await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) == 1
                    ? EntryExportCancellationResult.CancellationRequested
                    : EntryExportCancellationResult.AlreadyTerminal;
                break;
            case "COMPLETED":
            case "FAILED":
            case "CANCELLED":
                result = EntryExportCancellationResult.AlreadyTerminal;
                break;
            default:
                throw new InvalidDataException(
                    "Stored export task status is invalid.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<EntryExportTask?> GetAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumnsSql + """

            WHERE idempotency_key=$idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTask(reader)
            : null;
    }

    public async Task<IReadOnlyList<EntryExportTask>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumnsSql + """

            ORDER BY updated_at DESC, idempotency_key
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maximumCount);
        var tasks = new List<EntryExportTask>(maximumCount);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tasks.Add(ReadTask(reader));
        }
        return Array.AsReadOnly(tasks.ToArray());
    }

    private async Task FinishLeaseAsync(
        EntryExportTaskLease task,
        string status,
        EntryExportTaskErrorCode? errorCode,
        DateTimeOffset? nextAttemptAt,
        DateTimeOffset? completedAt,
        DateTimeOffset updatedAt,
        bool allowCancellationRequest,
        CancellationToken cancellationToken)
    {
        ValidateLease(task);
        await using SqliteConnection connection = await database.OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE entry_export_tasks
            SET status=$status,
                next_attempt_at=$nextAttemptAt,
                lease_token=NULL,
                lease_expires_at=NULL,
                cancellation_requested=0,
                last_error_code=$errorCode,
                completed_at=$completedAt,
                updated_at=$updatedAt
            WHERE idempotency_key=$idempotencyKey
              AND status='RUNNING'
              AND lease_token=$leaseToken
              AND ($allowCancellationRequest=1
                   OR cancellation_requested=0);
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue(
            "$nextAttemptAt",
            nextAttemptAt is null ? DBNull.Value : Format(nextAttemptAt.Value));
        command.Parameters.AddWithValue(
            "$errorCode",
            errorCode is null ? DBNull.Value : errorCode.ToString());
        command.Parameters.AddWithValue(
            "$completedAt",
            completedAt is null ? DBNull.Value : Format(completedAt.Value));
        command.Parameters.AddWithValue("$updatedAt", Format(updatedAt));
        command.Parameters.AddWithValue(
            "$allowCancellationRequest",
            allowCancellationRequest ? 1 : 0);
        AddLeaseParameters(command, task);
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw StaleLease();
        }
    }

    private static EntryExportTask ReadTask(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseViewKind(reader.GetString(5)),
            reader.GetInt64(6),
            ParseStatus(reader.GetString(7)),
            reader.GetInt32(8),
            ParseNullableTimestamp(reader, 9),
            reader.IsDBNull(10)
                ? null
                : ParseErrorCode(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)),
            ParseTimestamp(reader.GetString(12)),
            ParseNullableTimestamp(reader, 13));

    private static void ValidateRequest(EntryExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdempotencyKey(request.IdempotencyKey);
        ValidateText(request.ExporterId, 64, nameof(request));
        ValidateText(request.TargetId, 256, nameof(request));
        ArgumentNullException.ThrowIfNull(request.Entry);
        ValidateText(request.Entry.Id, 128, nameof(request));
        ValidateText(request.Entry.ContentHash, 128, nameof(request));
        if (!Enum.IsDefined(request.ViewKind))
        {
            throw new InvalidDataException(
                "The export request view kind is invalid.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(request.ContentBytes);
        EntryExportRequest expected = EntryExportRequest.Create(
            request.ExporterId,
            request.TargetId,
            request.Entry,
            request.ViewKind,
            request.ContentBytes);
        if (!string.Equals(
                expected.IdempotencyKey,
                request.IdempotencyKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The export request idempotency key is invalid.");
        }
    }

    private static void ValidateLease(EntryExportTaskLease task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateIdempotencyKey(task.IdempotencyKey);
        if (!Guid.TryParseExact(task.LeaseToken, "D", out _)
            || task.AttemptCount < 1)
        {
            throw new InvalidDataException(
                "The export task lease metadata is invalid.");
        }
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (idempotencyKey.Length != 64
            || idempotencyKey.Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The export idempotency key is invalid.",
                nameof(idempotencyKey));
        }
    }

    private static void ValidateText(
        string value,
        int maximumLength,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Export task metadata is invalid.",
                parameterName);
        }
    }

    private static void ValidateErrorCode(EntryExportTaskErrorCode errorCode)
    {
        if (!Enum.IsDefined(errorCode))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }
    }

    private static void AddRequestParameters(
        SqliteCommand command,
        EntryExportRequest request)
    {
        command.Parameters.AddWithValue(
            "$idempotencyKey",
            request.IdempotencyKey);
        command.Parameters.AddWithValue("$exporterId", request.ExporterId);
        command.Parameters.AddWithValue("$targetId", request.TargetId);
        command.Parameters.AddWithValue("$entryId", request.Entry.Id);
        command.Parameters.AddWithValue(
            "$contentHash",
            request.Entry.ContentHash);
        command.Parameters.AddWithValue(
            "$viewKind",
            request.ViewKind.ToString());
        command.Parameters.AddWithValue(
            "$contentBytes",
            request.ContentBytes);
    }

    private static void AddLeaseParameters(
        SqliteCommand command,
        EntryExportTaskLease task)
    {
        command.Parameters.AddWithValue(
            "$idempotencyKey",
            task.IdempotencyKey);
        command.Parameters.AddWithValue("$leaseToken", task.LeaseToken);
    }

    private static EntryViewKind ParseViewKind(string value) =>
        Enum.TryParse(value, ignoreCase: false, out EntryViewKind parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                "Stored export task view kind is invalid.");

    private static EntryExportTaskStatus ParseStatus(string value) =>
        value switch
        {
            "QUEUED" => EntryExportTaskStatus.Queued,
            "RUNNING" => EntryExportTaskStatus.Running,
            "COMPLETED" => EntryExportTaskStatus.Completed,
            "FAILED" => EntryExportTaskStatus.Failed,
            "CANCELLED" => EntryExportTaskStatus.Cancelled,
            _ => throw new InvalidDataException(
                "Stored export task status is invalid.")
        };

    private static EntryExportTaskErrorCode ParseErrorCode(string value) =>
        Enum.TryParse(
            value,
            ignoreCase: false,
            out EntryExportTaskErrorCode parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                "Stored export task error code is invalid.");

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseNullableTimestamp(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : ParseTimestamp(reader.GetString(ordinal));

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static InvalidOperationException StaleLease() =>
        new("The export task lease is no longer current.");

    private const string SelectColumnsSql = """
        SELECT idempotency_key, exporter_id, target_id, entry_id, content_hash,
               view_kind, content_bytes, status, attempt_count, next_attempt_at,
               last_error_code, created_at, updated_at, completed_at
        FROM entry_export_tasks
        """;

    private sealed record ExportCandidate(
        string IdempotencyKey,
        string ExporterId,
        string TargetId,
        string EntryId,
        string ContentHash,
        EntryViewKind ViewKind,
        long ContentBytes,
        int AttemptCount);
}
