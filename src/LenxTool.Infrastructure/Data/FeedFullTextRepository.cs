using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedFullTextRepository(SqliteDatabase database) :
    IFeedFullTextRepository,
    IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _claimGate = new(1, 1);
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _claimGate.Dispose();
    }

    public Task<IReadOnlyList<FeedFullTextWorkItem>> ClaimBackgroundAsync(
        DateTimeOffset now,
        int maximumCount,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) => ClaimAsync(
        entryId: null,
        backgroundOnly: true,
        now,
        maximumCount,
        leaseDuration,
        cancellationToken);

    public async Task<FeedFullTextWorkItem?> ClaimOnOpenAsync(
        string entryId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateEntryId(entryId);
        IReadOnlyList<FeedFullTextWorkItem> claimed = await ClaimAsync(
            entryId,
            backgroundOnly: false,
            now,
            maximumCount: 1,
            leaseDuration,
            cancellationToken).ConfigureAwait(false);
        return claimed.Count == 0 ? null : claimed[0];
    }

    public async Task<FeedFullTextContent?> GetContentAsync(
        string entryId,
        CancellationToken cancellationToken)
    {
        ValidateEntryId(entryId);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT article_json, content_hash, extracted_at
            FROM feed_full_text_content
            WHERE entry_id=$entryId;
            """;
        command.Parameters.AddWithValue("$entryId", entryId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            ArticleContentResult article = JsonSerializer.Deserialize<ArticleContentResult>(
                reader.GetString(0),
                JsonOptions) ?? throw new InvalidDataException("Stored article content is empty.");
            return new(
                entryId,
                article,
                reader.GetString(1),
                ReadTimestamp(reader, 2));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored article content is invalid.", exception);
        }
    }

    public async Task SaveContentAsync(
        FeedFullTextWorkItem workItem,
        ArticleContentResult article,
        DateTimeOffset extractedAt,
        CancellationToken cancellationToken)
    {
        ValidateWorkItem(workItem);
        ArgumentNullException.ThrowIfNull(article);
        string json = JsonSerializer.Serialize(article, JsonOptions);
        string contentHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand content = connection.CreateCommand();
        content.Transaction = transaction;
        content.CommandText = """
            INSERT INTO feed_full_text_content(entry_id, article_json, content_hash, extracted_at)
            SELECT $entryId, $articleJson, $contentHash, $extractedAt
            WHERE EXISTS (
                SELECT 1
                FROM feed_full_text_jobs
                WHERE entry_id=$entryId
                  AND status='IN_PROGRESS'
                  AND lease_id=$leaseId)
            ON CONFLICT(entry_id) DO UPDATE SET
                article_json=excluded.article_json,
                content_hash=excluded.content_hash,
                extracted_at=excluded.extracted_at;
            """;
        content.Parameters.AddWithValue("$entryId", workItem.EntryId);
        content.Parameters.AddWithValue("$leaseId", workItem.LeaseId);
        content.Parameters.AddWithValue("$articleJson", json);
        content.Parameters.AddWithValue("$contentHash", contentHash);
        content.Parameters.AddWithValue("$extractedAt", FormatTimestamp(extractedAt));
        int written = await content.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (written != 1)
        {
            throw new InvalidOperationException("The full-text work item is no longer claimed.");
        }

        await using SqliteCommand job = connection.CreateCommand();
        job.Transaction = transaction;
        job.CommandText = """
            UPDATE feed_full_text_jobs
            SET status='SUCCEEDED',
                next_attempt_at=NULL,
                lease_expires_at=NULL,
                lease_id=NULL,
                last_error_code=NULL,
                updated_at=$updatedAt
            WHERE entry_id=$entryId AND status='IN_PROGRESS' AND lease_id=$leaseId;
            DELETE FROM feed_full_text_host_state WHERE host=$host;
            """;
        job.Parameters.AddWithValue("$entryId", workItem.EntryId);
        job.Parameters.AddWithValue("$leaseId", workItem.LeaseId);
        job.Parameters.AddWithValue("$host", workItem.Host);
        job.Parameters.AddWithValue("$updatedAt", FormatTimestamp(extractedAt));
        await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ScheduleRetryAsync(
        FeedFullTextWorkItem workItem,
        string errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken) => SaveFailureAsync(
        workItem,
        errorCode,
        status: "RETRY",
        nextAttemptAt,
        failedAt,
        cancellationToken);

    public Task BlockAsync(
        FeedFullTextWorkItem workItem,
        string errorCode,
        DateTimeOffset blockedAt,
        DateTimeOffset hostRetryAt,
        CancellationToken cancellationToken) => SaveFailureAsync(
        workItem,
        errorCode,
        status: "BLOCKED",
        hostRetryAt,
        blockedAt,
        cancellationToken);

    public async Task ReleaseAsync(
        FeedFullTextWorkItem workItem,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        ValidateWorkItem(workItem);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE feed_full_text_jobs
            SET status='RETRY',
                next_attempt_at=$releasedAt,
                lease_expires_at=NULL,
                lease_id=NULL,
                last_error_code=NULL,
                updated_at=$releasedAt
            WHERE entry_id=$entryId AND status='IN_PROGRESS' AND lease_id=$leaseId;
            """;
        command.Parameters.AddWithValue("$entryId", workItem.EntryId);
        command.Parameters.AddWithValue("$leaseId", workItem.LeaseId);
        command.Parameters.AddWithValue("$releasedAt", FormatTimestamp(releasedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FeedFullTextWorkItem>> ClaimAsync(
        string? entryId,
        bool backgroundOnly,
        DateTimeOffset now,
        int maximumCount,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumCount is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        await _claimGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await DeleteExpiredHostBackoffsAsync(
                connection,
                transaction,
                now,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, DateTimeOffset> hostBackoffs =
                await ReadHostBackoffsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            ISet<string> activeHosts = await ReadActiveHostsAsync(
                connection,
                transaction,
                now,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<Candidate> candidates = await ReadCandidatesAsync(
                connection,
                transaction,
                entryId,
                backgroundOnly,
                now,
                Math.Min(400, maximumCount * 4),
                cancellationToken).ConfigureAwait(false);

            var claimed = new List<FeedFullTextWorkItem>(maximumCount);
            foreach (Candidate candidate in candidates)
            {
                if (claimed.Count == maximumCount) break;
                if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out Uri? uri)
                    || uri.Scheme is not ("http" or "https")
                    || string.IsNullOrWhiteSpace(uri.IdnHost))
                {
                    continue;
                }
                string host = uri.IdnHost.ToLowerInvariant();
                if (activeHosts.Contains(host)
                    || (hostBackoffs.TryGetValue(host, out DateTimeOffset nextHostAttempt)
                        && nextHostAttempt > now))
                {
                    continue;
                }

                string? leaseId = await TryClaimAsync(
                    connection,
                    transaction,
                    candidate,
                    host,
                    now,
                    now.Add(leaseDuration),
                    cancellationToken).ConfigureAwait(false);
                if (leaseId is not null)
                {
                    activeHosts.Add(host);
                    claimed.Add(new(
                        candidate.EntryId,
                        candidate.FeedId,
                        candidate.Url,
                        host,
                        candidate.AttemptCount,
                        leaseId));
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claimed;
        }
        finally
        {
            _claimGate.Release();
        }
    }

    private static async Task<IReadOnlyList<Candidate>> ReadCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? entryId,
        bool backgroundOnly,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.id, e.feed_id, e.normalized_url, COALESCE(j.attempt_count, 0)
            FROM feed_entries e
            JOIN feed_catalog f ON f.id=e.feed_id
            LEFT JOIN feed_categories c ON c.id=f.category_id
            LEFT JOIN feed_full_text_content content ON content.entry_id=e.id
            LEFT JOIN feed_full_text_jobs j ON j.entry_id=e.id
            WHERE ($entryId IS NULL OR e.id=$entryId)
              AND f.is_enabled=1
              AND (f.category_id IS NULL OR c.is_enabled=1)
              AND (
                    ($backgroundOnly=1 AND f.full_text_policy='BACKGROUND')
                    OR ($backgroundOnly=0 AND f.full_text_policy IN ('ON_OPEN', 'BACKGROUND')))
              AND e.has_full_content=0
              AND e.normalized_url IS NOT NULL
              AND content.entry_id IS NULL
              AND (
                    j.entry_id IS NULL
                    OR j.status IN ('PENDING', 'RETRY')
                       AND (j.next_attempt_at IS NULL OR julianday(j.next_attempt_at) <= julianday($now))
                    OR j.status='IN_PROGRESS'
                       AND j.lease_expires_at IS NOT NULL
                       AND julianday(j.lease_expires_at) <= julianday($now))
            ORDER BY julianday(COALESCE(e.published_at, e.updated_at, e.fetched_at)) DESC, e.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$entryId", (object?)entryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$backgroundOnly", backgroundOnly ? 1 : 0);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$limit", limit);
        var candidates = new List<Candidate>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }
        return candidates;
    }

    private static async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadHostBackoffsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT host, next_attempt_at FROM feed_full_text_host_state;";
        var states = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            states[reader.GetString(0)] = ReadTimestamp(reader, 1);
        }
        return states;
    }

    private static async Task DeleteExpiredHostBackoffsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM feed_full_text_host_state
            WHERE julianday(next_attempt_at) <= julianday($now);
            """;
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ISet<string>> ReadActiveHostsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT host
            FROM feed_full_text_jobs
            WHERE status='IN_PROGRESS'
              AND lease_expires_at IS NOT NULL
              AND julianday(lease_expires_at) > julianday($now);
            """;
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hosts.Add(reader.GetString(0));
        }
        return hosts;
    }

    private static async Task<string?> TryClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Candidate candidate,
        string host,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        string leaseId = Guid.NewGuid().ToString("D");
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO feed_full_text_jobs(
                entry_id, host, status, attempt_count, next_attempt_at,
                lease_expires_at, lease_id, last_error_code, updated_at)
            VALUES(
                $entryId, $host, 'IN_PROGRESS', $attemptCount, NULL,
                $leaseExpiresAt, $leaseId, NULL, $now)
            ON CONFLICT(entry_id) DO UPDATE SET
                host=excluded.host,
                status='IN_PROGRESS',
                next_attempt_at=NULL,
                lease_expires_at=excluded.lease_expires_at,
                lease_id=excluded.lease_id,
                last_error_code=NULL,
                updated_at=excluded.updated_at
            WHERE feed_full_text_jobs.status IN ('PENDING', 'RETRY')
               OR (feed_full_text_jobs.status='IN_PROGRESS'
                   AND feed_full_text_jobs.lease_expires_at IS NOT NULL
                   AND julianday(feed_full_text_jobs.lease_expires_at) <= julianday($now));
            """;
        command.Parameters.AddWithValue("$entryId", candidate.EntryId);
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$attemptCount", candidate.AttemptCount);
        command.Parameters.AddWithValue("$leaseExpiresAt", FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$leaseId", leaseId);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
            ? leaseId
            : null;
    }

    private async Task SaveFailureAsync(
        FeedFullTextWorkItem workItem,
        string errorCode,
        string status,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        ValidateWorkItem(workItem);
        ValidateErrorCode(errorCode);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand job = connection.CreateCommand();
        job.Transaction = transaction;
        job.CommandText = """
            UPDATE feed_full_text_jobs
            SET status=$status,
                attempt_count=attempt_count+1,
                next_attempt_at=$nextAttemptAt,
                lease_expires_at=NULL,
                lease_id=NULL,
                last_error_code=$errorCode,
                updated_at=$failedAt
            WHERE entry_id=$entryId AND status='IN_PROGRESS' AND lease_id=$leaseId;
            """;
        job.Parameters.AddWithValue("$status", status);
        job.Parameters.AddWithValue("$nextAttemptAt", FormatTimestamp(nextAttemptAt));
        job.Parameters.AddWithValue("$errorCode", errorCode);
        job.Parameters.AddWithValue("$failedAt", FormatTimestamp(failedAt));
        job.Parameters.AddWithValue("$entryId", workItem.EntryId);
        job.Parameters.AddWithValue("$leaseId", workItem.LeaseId);
        int changed = await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed == 1)
        {
            await using SqliteCommand host = connection.CreateCommand();
            host.Transaction = transaction;
            host.CommandText = """
                INSERT INTO feed_full_text_host_state(
                    host, consecutive_failures, next_attempt_at, last_error_code, updated_at)
                VALUES($host, 1, $nextAttemptAt, $errorCode, $failedAt)
                ON CONFLICT(host) DO UPDATE SET
                    consecutive_failures=feed_full_text_host_state.consecutive_failures+1,
                    next_attempt_at=CASE
                        WHEN julianday(excluded.next_attempt_at) > julianday(feed_full_text_host_state.next_attempt_at)
                        THEN excluded.next_attempt_at
                        ELSE feed_full_text_host_state.next_attempt_at
                    END,
                    last_error_code=excluded.last_error_code,
                    updated_at=excluded.updated_at;
                """;
            host.Parameters.AddWithValue("$host", workItem.Host);
            host.Parameters.AddWithValue("$nextAttemptAt", FormatTimestamp(nextAttemptAt));
            host.Parameters.AddWithValue("$errorCode", errorCode);
            host.Parameters.AddWithValue("$failedAt", FormatTimestamp(failedAt));
            await host.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateWorkItem(FeedFullTextWorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ValidateEntryId(workItem.EntryId);
        if (!Guid.TryParseExact(workItem.FeedId, "D", out _)
            || !Uri.TryCreate(workItem.Url, UriKind.Absolute, out _)
            || string.IsNullOrWhiteSpace(workItem.Host)
            || workItem.Host.Length > 253
            || workItem.AttemptCount < 0
            || !Guid.TryParseExact(workItem.LeaseId, "D", out _))
        {
            throw new ArgumentException("Full-text work item is invalid.", nameof(workItem));
        }
    }

    private static void ValidateEntryId(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)
            || entryId.Length > 128
            || entryId.Any(char.IsControl))
        {
            throw new ArgumentException("Entry identifier is invalid.", nameof(entryId));
        }
    }

    private static void ValidateErrorCode(string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode)
            || errorCode.Length > 128
            || errorCode.Any(char.IsControl))
        {
            throw new ArgumentException("Error code is invalid.", nameof(errorCode));
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private sealed record Candidate(
        string EntryId,
        string FeedId,
        string Url,
        int AttemptCount);
}
