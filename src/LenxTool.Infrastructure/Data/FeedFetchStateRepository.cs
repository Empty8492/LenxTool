using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedFetchStateRepository(SqliteDatabase database) : IFeedFetchStateRepository
{
    public async Task<FeedRefreshTarget?> GetTargetAsync(
        string feedId,
        CancellationToken cancellationToken)
    {
        ValidateFeedId(feedId);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = CreateTargetCommand(connection);
        command.CommandText += " AND f.id=$feedId LIMIT 1;";
        command.Parameters.AddWithValue("$feedId", feedId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTarget(reader)
            : null;
    }

    public async Task<IReadOnlyList<FeedRefreshTarget>> GetDueTargetsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = CreateTargetCommand(connection);
        command.CommandText += """
             AND (fs.next_fetch_at IS NULL OR fs.next_fetch_at <= $now)
            ORDER BY COALESCE(fs.next_fetch_at, ''), f.sort_order, f.id
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        var targets = new List<FeedRefreshTarget>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            targets.Add(ReadTarget(reader));
        }
        return targets;
    }

    public async Task<IReadOnlyList<FeedRefreshTarget>> GetAllTargetsAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = CreateTargetCommand(connection, activeOnly: false);
        command.CommandText += " ORDER BY f.sort_order, f.id;";
        var targets = new List<FeedRefreshTarget>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            targets.Add(ReadTarget(reader));
        }
        return targets;
    }

    public async Task<bool> SaveStateAsync(
        FeedFetchState state,
        CancellationToken cancellationToken)
    {
        ValidateState(state);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO feed_fetch_state(
                feed_id, etag, last_modified, next_fetch_at, last_success_at, last_failure_at,
                consecutive_failures, error_code, updated_at)
            SELECT
                $feedId, $etag, $lastModified, $nextFetchAt, $lastSuccessAt, $lastFailureAt,
                $consecutiveFailures, $errorCode, $updatedAt
            FROM feed_catalog
            WHERE id=$feedId
            ON CONFLICT(feed_id) DO UPDATE SET
                etag=excluded.etag,
                last_modified=excluded.last_modified,
                next_fetch_at=excluded.next_fetch_at,
                last_success_at=excluded.last_success_at,
                last_failure_at=excluded.last_failure_at,
                consecutive_failures=excluded.consecutive_failures,
                error_code=excluded.error_code,
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$feedId", state.FeedId);
        command.Parameters.AddWithValue("$etag", (object?)state.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastModified", (object?)state.LastModified ?? DBNull.Value);
        command.Parameters.AddWithValue("$nextFetchAt", FormatNullableTimestamp(state.NextFetchAt));
        command.Parameters.AddWithValue("$lastSuccessAt", FormatNullableTimestamp(state.LastSuccessAt));
        command.Parameters.AddWithValue("$lastFailureAt", FormatNullableTimestamp(state.LastFailureAt));
        command.Parameters.AddWithValue("$consecutiveFailures", state.ConsecutiveFailures);
        command.Parameters.AddWithValue("$errorCode", (object?)state.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(state.UpdatedAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static SqliteCommand CreateTargetCommand(
        SqliteConnection connection,
        bool activeOnly = true)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                f.id, f.original_url, f.normalized_url, f.display_name, f.site_url,
                f.category_id, f.view_kind, f.refresh_interval_minutes, f.sort_order,
                f.is_enabled, f.version, f.created_at, f.updated_at,
                fs.feed_id, fs.etag, fs.last_modified, fs.next_fetch_at, fs.last_success_at,
                fs.last_failure_at, fs.consecutive_failures, fs.error_code, fs.updated_at
            FROM feed_catalog f
            LEFT JOIN feed_categories c ON c.id=f.category_id
            LEFT JOIN feed_fetch_state fs ON fs.feed_id=f.id
            """;
        if (activeOnly)
        {
            command.CommandText += """
                WHERE f.is_enabled=1
                  AND (f.category_id IS NULL OR c.is_enabled=1)
                """;
        }
        return command;
    }

    private static FeedRefreshTarget ReadTarget(SqliteDataReader reader)
    {
        var feed = new FeedCatalogItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            ParseViewKind(reader.GetString(6)),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetBoolean(9),
            reader.GetInt64(10),
            ReadTimestamp(reader, 11),
            ReadTimestamp(reader, 12));
        FeedFetchState? state = reader.IsDBNull(13)
            ? null
            : new(
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                ReadNullableTimestamp(reader, 16),
                ReadNullableTimestamp(reader, 17),
                ReadNullableTimestamp(reader, 18),
                reader.GetInt32(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                ReadTimestamp(reader, 21));
        return new(feed, state);
    }

    private static void ValidateState(FeedFetchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateFeedId(state.FeedId);
        if (state.ETag is { Length: > 1024 }
            || state.LastModified is { Length: > 256 }
            || state.ConsecutiveFailures < 0
            || state.ErrorCode is { Length: > 128 }
            || HasControlCharacters(state.ETag)
            || HasControlCharacters(state.LastModified)
            || HasControlCharacters(state.ErrorCode))
        {
            throw new ArgumentException("Feed fetch state contains an invalid bounded value.", nameof(state));
        }
    }

    private static bool HasControlCharacters(string? value) => value?.Any(char.IsControl) == true;

    private static void ValidateFeedId(string feedId)
    {
        if (!Guid.TryParseExact(feedId, "D", out _))
            throw new ArgumentException("Feed ID must be a canonical GUID.", nameof(feedId));
    }

    private static FeedViewKind ParseViewKind(string value) => value switch
    {
        "ARTICLE" => FeedViewKind.Article,
        "PICTURE" => FeedViewKind.Picture,
        "AUDIO" => FeedViewKind.Audio,
        "VIDEO" => FeedViewKind.Video,
        "NOTIFICATION" => FeedViewKind.Notification,
        _ => throw new InvalidDataException($"Unknown feed view kind '{value}'.")
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullableTimestamp(DateTimeOffset? value) =>
        value is null ? DBNull.Value : FormatTimestamp(value.Value);

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadTimestamp(reader, ordinal);
}
