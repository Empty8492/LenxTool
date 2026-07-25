using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedAiAutomationJobRepository(SqliteDatabase database)
    : IFeedAiAutomationJobRepository
{
    public async Task<int> EnqueueAsync(
        string feedId,
        IReadOnlyList<FeedEntry> entries,
        ResolvedFeedAiPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidateGuid(feedId, nameof(feedId));
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(policy);
        if (entries.Count > 2000) throw new ArgumentOutOfRangeException(nameof(entries));
        ValidatePolicy(policy);
        foreach (FeedEntry entry in entries) ValidateEntry(feedId, entry);

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        string timestamp = Format(now);

        await ApplyPolicyToQueuedJobsAsync(
            command,
            feedId,
            policy,
            timestamp,
            cancellationToken).ConfigureAwait(false);

        int enqueued = 0;
        foreach (FeedEntry entry in entries
                     .OrderByDescending(item => item.PublishedAt ?? item.UpdatedAt ?? item.FetchedAt)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE feed_ai_automation_jobs
                SET status='SUPERSEDED', lease_token=NULL, lease_expires_at=NULL,
                    last_error_code='CONTENT_CHANGED', updated_at=$updatedAt
                WHERE feed_id=$feedId
                  AND entry_id=$entryId
                  AND content_hash<>$contentHash
                  AND status IN ('PENDING', 'RUNNING', 'RETRY');
                """;
            command.Parameters.AddWithValue("$feedId", feedId);
            command.Parameters.AddWithValue("$entryId", entry.Id);
            command.Parameters.AddWithValue("$contentHash", entry.ContentHash);
            command.Parameters.AddWithValue("$updatedAt", timestamp);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (policy.AutoSummaryEnabled)
            {
                enqueued += await EnqueueTaskAsync(
                    command,
                    feedId,
                    entry,
                    FeedAiAutomationTaskType.Summary,
                    "und",
                    timestamp,
                    cancellationToken).ConfigureAwait(false);
            }

            if (policy.AutoTranslationEnabled)
            {
                enqueued += await EnqueueTaskAsync(
                    command,
                    feedId,
                    entry,
                    FeedAiAutomationTaskType.Translation,
                    policy.TranslationTargetLanguage,
                    timestamp,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return enqueued;
    }

    public async Task<IReadOnlyList<FeedAiAutomationJob>> ClaimDueAsync(
        DateTimeOffset now,
        int maximumCount,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        string timestamp = Format(now);
        command.CommandText = """
            SELECT id, feed_id, entry_id, content_hash, task_type, target_language, attempt_count
            FROM feed_ai_automation_jobs
            WHERE (status IN ('PENDING', 'RETRY') AND next_attempt_at<=$now)
               OR (status='RUNNING' AND lease_expires_at<=$now)
            ORDER BY next_attempt_at, created_at, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", timestamp);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var candidates = new List<JobCandidate>(maximumCount);
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    ParseTaskType(reader.GetString(4)),
                    reader.GetString(5),
                    reader.GetInt32(6)));
            }
        }

        var jobs = new List<FeedAiAutomationJob>(candidates.Count);
        foreach (JobCandidate candidate in candidates)
        {
            string leaseToken = Guid.NewGuid().ToString("N");
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE feed_ai_automation_jobs
                SET status='RUNNING',
                    attempt_count=attempt_count+1,
                    lease_token=$leaseToken,
                    lease_expires_at=$leaseExpiresAt,
                    updated_at=$updatedAt
                WHERE id=$id
                  AND ((status IN ('PENDING', 'RETRY') AND next_attempt_at<=$now)
                    OR (status='RUNNING' AND lease_expires_at<=$now));
                """;
            command.Parameters.AddWithValue("$leaseToken", leaseToken);
            command.Parameters.AddWithValue("$leaseExpiresAt", Format(now.Add(leaseDuration)));
            command.Parameters.AddWithValue("$updatedAt", timestamp);
            command.Parameters.AddWithValue("$id", candidate.Id);
            command.Parameters.AddWithValue("$now", timestamp);
            int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed == 1)
            {
                jobs.Add(new(
                    candidate.Id,
                    candidate.FeedId,
                    candidate.EntryId,
                    candidate.ContentHash,
                    candidate.TaskType,
                    candidate.TargetLanguage,
                    checked(candidate.AttemptCount + 1),
                    leaseToken));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return jobs;
    }

    public async Task<bool> TryReserveDailyEntryAsync(
        DateOnly usageDate,
        string feedId,
        string entryId,
        int dailyEntryLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidateGuid(feedId, nameof(feedId));
        ValidateText(entryId, nameof(entryId), 256);
        if (dailyEntryLimit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(dailyEntryLimit));

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        string date = usageDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        command.CommandText = """
            DELETE FROM feed_ai_automation_daily_entries
            WHERE usage_date < $retentionCutoff;
            """;
        command.Parameters.AddWithValue(
            "$retentionCutoff",
            usageDate.AddDays(-31).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = """
            INSERT OR IGNORE INTO feed_ai_automation_daily_entries(
                usage_date, feed_id, entry_id, reserved_at)
            SELECT $usageDate, $feedId, $entryId, $reservedAt
            WHERE (
                SELECT COUNT(*)
                FROM feed_ai_automation_daily_entries
                WHERE usage_date=$usageDate AND feed_id=$feedId
            ) < $dailyEntryLimit;
            """;
        command.Parameters.AddWithValue("$usageDate", date);
        command.Parameters.AddWithValue("$feedId", feedId);
        command.Parameters.AddWithValue("$entryId", entryId);
        command.Parameters.AddWithValue("$reservedAt", Format(now));
        command.Parameters.AddWithValue("$dailyEntryLimit", dailyEntryLimit);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM feed_ai_automation_daily_entries
                WHERE usage_date=$usageDate AND feed_id=$feedId AND entry_id=$entryId);
            """;
        command.Parameters.AddWithValue("$usageDate", date);
        command.Parameters.AddWithValue("$feedId", feedId);
        command.Parameters.AddWithValue("$entryId", entryId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    public Task CompleteAsync(
        FeedAiAutomationJob job,
        FeedAiAutomationJobOutcome outcome,
        string? errorCode,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken) =>
        FinishLeaseAsync(
            job,
            outcome switch
            {
                FeedAiAutomationJobOutcome.Succeeded => "SUCCEEDED",
                FeedAiAutomationJobOutcome.Skipped => "SKIPPED",
                FeedAiAutomationJobOutcome.Superseded => "SUPERSEDED",
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            },
            errorCode,
            completedAt,
            nextAttemptAt: null,
            cancellationToken);

    public Task ScheduleRetryAsync(
        FeedAiAutomationJob job,
        string errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nextAttemptAt, failedAt);
        return FinishLeaseAsync(
            job,
            "RETRY",
            errorCode,
            failedAt,
            nextAttemptAt,
            cancellationToken);
    }

    public Task ReleaseAsync(
        FeedAiAutomationJob job,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken) =>
        FinishLeaseAsync(
            job,
            "PENDING",
            null,
            releasedAt,
            releasedAt,
            cancellationToken);

    private static async Task ApplyPolicyToQueuedJobsAsync(
        SqliteCommand command,
        string feedId,
        ResolvedFeedAiPolicy policy,
        string timestamp,
        CancellationToken cancellationToken)
    {
        command.CommandText = """
            UPDATE feed_ai_automation_jobs
            SET status='SKIPPED', lease_token=NULL, lease_expires_at=NULL,
                last_error_code='POLICY_DISABLED', updated_at=$updatedAt
            WHERE feed_id=$feedId
              AND status IN ('PENDING', 'RUNNING', 'RETRY')
              AND ((task_type='SUMMARY' AND $summaryEnabled=0)
                OR (task_type='TRANSLATION' AND $translationEnabled=0));
            """;
        command.Parameters.AddWithValue("$updatedAt", timestamp);
        command.Parameters.AddWithValue("$feedId", feedId);
        command.Parameters.AddWithValue("$summaryEnabled", policy.AutoSummaryEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$translationEnabled", policy.AutoTranslationEnabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (!policy.AutoTranslationEnabled) return;
        command.Parameters.Clear();
        command.CommandText = """
            UPDATE feed_ai_automation_jobs
            SET status='SUPERSEDED', lease_token=NULL, lease_expires_at=NULL,
                last_error_code='TARGET_CHANGED', updated_at=$updatedAt
            WHERE feed_id=$feedId
              AND task_type='TRANSLATION'
              AND target_language<>$targetLanguage
              AND status IN ('PENDING', 'RUNNING', 'RETRY');
            """;
        command.Parameters.AddWithValue("$updatedAt", timestamp);
        command.Parameters.AddWithValue("$feedId", feedId);
        command.Parameters.AddWithValue("$targetLanguage", policy.TranslationTargetLanguage);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> EnqueueTaskAsync(
        SqliteCommand command,
        string feedId,
        FeedEntry entry,
        FeedAiAutomationTaskType taskType,
        string targetLanguage,
        string timestamp,
        CancellationToken cancellationToken)
    {
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO feed_ai_automation_jobs(
                id, feed_id, entry_id, content_hash, task_type, target_language,
                status, attempt_count, next_attempt_at, created_at, updated_at)
            VALUES(
                $id, $feedId, $entryId, $contentHash, $taskType, $targetLanguage,
                'PENDING', 0, $nextAttemptAt, $createdAt, $updatedAt)
            ON CONFLICT(feed_id, entry_id, content_hash, task_type, target_language)
            DO UPDATE SET
                status='PENDING',
                next_attempt_at=excluded.next_attempt_at,
                lease_token=NULL,
                lease_expires_at=NULL,
                last_error_code=NULL,
                updated_at=excluded.updated_at
            WHERE feed_ai_automation_jobs.status IN ('SKIPPED', 'SUPERSEDED');
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$feedId", feedId);
        command.Parameters.AddWithValue("$entryId", entry.Id);
        command.Parameters.AddWithValue("$contentHash", entry.ContentHash);
        command.Parameters.AddWithValue("$taskType", StoreTaskType(taskType));
        command.Parameters.AddWithValue("$targetLanguage", targetLanguage);
        command.Parameters.AddWithValue("$nextAttemptAt", timestamp);
        command.Parameters.AddWithValue("$createdAt", timestamp);
        command.Parameters.AddWithValue("$updatedAt", timestamp);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishLeaseAsync(
        FeedAiAutomationJob job,
        string status,
        string? errorCode,
        DateTimeOffset updatedAt,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken)
    {
        ValidateJob(job);
        if (errorCode is not null) ValidateText(errorCode, nameof(errorCode), 128);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE feed_ai_automation_jobs
            SET status=$status,
                next_attempt_at=COALESCE($nextAttemptAt, next_attempt_at),
                lease_token=NULL,
                lease_expires_at=NULL,
                last_error_code=$errorCode,
                updated_at=$updatedAt
            WHERE id=$id AND status='RUNNING' AND lease_token=$leaseToken;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue(
            "$nextAttemptAt",
            nextAttemptAt is null ? DBNull.Value : Format(nextAttemptAt.Value));
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", Format(updatedAt));
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$leaseToken", job.LeaseToken);
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidOperationException("The Feed AI automation lease is no longer current.");
    }

    private static void ValidatePolicy(ResolvedFeedAiPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.TranslationTargetLanguage)
            || policy.TranslationTargetLanguage.Length > 32
            || policy.DailyEntryLimit is < 1 or > 1000
            || policy.MaxConcurrency is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }

    private static void ValidateEntry(string feedId, FeedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.Equals(entry.FeedId, feedId, StringComparison.Ordinal))
            throw new ArgumentException("Entry Feed ID does not match the queue Feed.", nameof(entry));
        ValidateText(entry.Id, nameof(entry.Id), 256);
        if (entry.ContentHash.Length != 64 || entry.ContentHash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Entry content hash must be a SHA-256 value.", nameof(entry));
    }

    private static void ValidateJob(FeedAiAutomationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateGuid(job.Id, nameof(job.Id));
        ValidateGuid(job.FeedId, nameof(job.FeedId));
        ValidateText(job.EntryId, nameof(job.EntryId), 256);
        ValidateText(job.LeaseToken, nameof(job.LeaseToken), 32);
        if (job.LeaseToken.Length != 32)
            throw new ArgumentOutOfRangeException(nameof(job));
    }

    private static void ValidateGuid(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out _))
            throw new ArgumentException("A canonical GUID is required.", parameterName);
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static string StoreTaskType(FeedAiAutomationTaskType taskType) =>
        taskType switch
        {
            FeedAiAutomationTaskType.Summary => "SUMMARY",
            FeedAiAutomationTaskType.Translation => "TRANSLATION",
            _ => throw new ArgumentOutOfRangeException(nameof(taskType))
        };

    private static FeedAiAutomationTaskType ParseTaskType(string value) =>
        value switch
        {
            "SUMMARY" => FeedAiAutomationTaskType.Summary,
            "TRANSLATION" => FeedAiAutomationTaskType.Translation,
            _ => throw new InvalidDataException("Stored Feed AI automation task type is invalid.")
        };

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private sealed record JobCandidate(
        string Id,
        string FeedId,
        string EntryId,
        string ContentHash,
        FeedAiAutomationTaskType TaskType,
        string TargetLanguage,
        int AttemptCount);
}
