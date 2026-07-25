using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedAutomationActionQueueRepository(SqliteDatabase database)
    : IFeedAutomationActionQueueRepository
{
    public async Task<IReadOnlyList<FeedAutomationActionLease>> ClaimDueAsync(
        DateTimeOffset now,
        int maximumCount,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        string timestamp = Format(now);
        command.CommandText = """
            SELECT idempotency_key, entry_id, rule_id, rule_version,
                   rule_priority, rule_conflict_order, action_type, action_order,
                   action_value, attempt_count
            FROM feed_automation_action_runs
            WHERE disposition='PLANNED'
              AND ((status IN ('PENDING', 'RETRY') AND next_attempt_at<=$now)
                OR (status='RUNNING' AND lease_expires_at<=$now))
            ORDER BY rule_priority DESC, rule_conflict_order,
                     rule_id, action_order, created_at, idempotency_key
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", timestamp);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var candidates = new List<ActionCandidate>(maximumCount);
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    ParseActionType(reader.GetString(6)),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetInt32(9)));
            }
        }

        var claimed = new List<FeedAutomationActionLease>(candidates.Count);
        foreach (ActionCandidate candidate in candidates)
        {
            string leaseToken = Guid.NewGuid().ToString("N");
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE feed_automation_action_runs
                SET status='RUNNING',
                    attempt_count=attempt_count+1,
                    next_attempt_at=NULL,
                    lease_token=$leaseToken,
                    lease_expires_at=$leaseExpiresAt,
                    updated_at=$updatedAt
                WHERE idempotency_key=$idempotencyKey
                  AND disposition='PLANNED'
                  AND ((status IN ('PENDING', 'RETRY') AND next_attempt_at<=$now)
                    OR (status='RUNNING' AND lease_expires_at<=$now));
                """;
            command.Parameters.AddWithValue("$leaseToken", leaseToken);
            command.Parameters.AddWithValue(
                "$leaseExpiresAt",
                Format(now.Add(leaseDuration)));
            command.Parameters.AddWithValue("$updatedAt", timestamp);
            command.Parameters.AddWithValue(
                "$idempotencyKey",
                candidate.IdempotencyKey);
            command.Parameters.AddWithValue("$now", timestamp);
            int changed = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (changed == 1)
            {
                claimed.Add(new(
                    candidate.IdempotencyKey,
                    candidate.EntryId,
                    candidate.RuleId,
                    candidate.RuleVersion,
                    candidate.RulePriority,
                    candidate.RuleConflictOrder,
                    candidate.Type,
                    candidate.ActionOrder,
                    candidate.Value,
                    checked(candidate.AttemptCount + 1),
                    leaseToken));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(claimed.ToArray());
    }

    public Task CompleteAsync(
        FeedAutomationActionLease action,
        FeedAutomationActionRunOutcome outcome,
        string? errorCode,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        if (outcome == FeedAutomationActionRunOutcome.Succeeded
            && errorCode is not null)
        {
            throw new ArgumentException(
                "A successful automation action cannot have an error code.",
                nameof(errorCode));
        }
        if (outcome == FeedAutomationActionRunOutcome.Failed
            && string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException(
                "A failed automation action requires an error code.",
                nameof(errorCode));
        }
        return FinishLeaseAsync(
            action,
            outcome == FeedAutomationActionRunOutcome.Succeeded
                ? "SUCCEEDED"
                : "FAILED",
            errorCode,
            completedAt,
            nextAttemptAt: null,
            cancellationToken);
    }

    public Task ScheduleRetryAsync(
        FeedAutomationActionLease action,
        string errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nextAttemptAt, failedAt);
        return FinishLeaseAsync(
            action,
            "RETRY",
            errorCode,
            failedAt,
            nextAttemptAt,
            cancellationToken);
    }

    public Task ReleaseAsync(
        FeedAutomationActionLease action,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken) =>
        FinishLeaseAsync(
            action,
            "PENDING",
            errorCode: null,
            releasedAt,
            releasedAt,
            cancellationToken);

    private async Task FinishLeaseAsync(
        FeedAutomationActionLease action,
        string status,
        string? errorCode,
        DateTimeOffset updatedAt,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken)
    {
        ValidateLease(action);
        if (errorCode is not null)
        {
            ValidateErrorCode(errorCode);
        }

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE feed_automation_action_runs
            SET status=$status,
                next_attempt_at=$nextAttemptAt,
                lease_token=NULL,
                lease_expires_at=NULL,
                last_error_code=$errorCode,
                updated_at=$updatedAt
            WHERE idempotency_key=$idempotencyKey
              AND status='RUNNING'
              AND lease_token=$leaseToken;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue(
            "$nextAttemptAt",
            nextAttemptAt is null
                ? DBNull.Value
                : Format(nextAttemptAt.Value));
        command.Parameters.AddWithValue(
            "$errorCode",
            (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Format(updatedAt));
        command.Parameters.AddWithValue(
            "$idempotencyKey",
            action.IdempotencyKey);
        command.Parameters.AddWithValue("$leaseToken", action.LeaseToken);
        int changed = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException(
                "The Feed automation action lease is no longer current.");
        }
    }

    private static void ValidateLease(FeedAutomationActionLease action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsLowerHex(action.IdempotencyKey, 64)
            || !IsLowerHex(action.LeaseToken, 32)
            || action.AttemptCount < 1)
        {
            throw new InvalidDataException(
                "Feed automation action lease metadata is invalid.");
        }
    }

    private static void ValidateErrorCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(char.IsControl)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Feed automation action error code is invalid.",
                nameof(value));
        }
    }

    private static bool IsLowerHex(string? value, int requiredLength) =>
        value is not null
        && value.Length == requiredLength
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static FeedAutomationActionType ParseActionType(string value) =>
        value switch
        {
            "ADD_TAG" => FeedAutomationActionType.AddTag,
            "HIDE" => FeedAutomationActionType.Hide,
            "MARK_READ" => FeedAutomationActionType.MarkRead,
            "GENERATE_SUMMARY" => FeedAutomationActionType.GenerateSummary,
            "TRANSLATE" => FeedAutomationActionType.Translate,
            "SEND_TO_MEDIA" => FeedAutomationActionType.SendToMedia,
            "NOTIFY" => FeedAutomationActionType.Notify,
            _ => throw new InvalidDataException(
                "Stored automation action type is invalid.")
        };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record ActionCandidate(
        string IdempotencyKey,
        string EntryId,
        string RuleId,
        int RuleVersion,
        int RulePriority,
        int RuleConflictOrder,
        FeedAutomationActionType Type,
        int ActionOrder,
        string? Value,
        int AttemptCount);
}
