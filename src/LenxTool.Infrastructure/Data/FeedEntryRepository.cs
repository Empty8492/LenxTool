using System.Globalization;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedEntryRepository(SqliteDatabase database) : IFeedEntryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task UpsertAsync(
        string feedId,
        IReadOnlyList<FeedEntry> entries,
        CancellationToken cancellationToken)
    {
        ValidateEntries(feedId, entries);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (FeedEntry entry in entries)
        {
            await UpsertEntryAsync(connection, transaction, entry, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeedEntry?> GetByIdAsync(
        string entryId,
        CancellationToken cancellationToken)
    {
        ValidateOptionalIdentifier(entryId, nameof(entryId));
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id, feed_id, external_id, normalized_url, title, author,
                published_at, updated_at, summary, sanitized_content,
                enclosure_json, content_hash, fetched_at, has_full_content
            FROM feed_entries
            WHERE id=$entryId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$entryId", entryId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadEntry(reader)
            : null;
    }

    public async Task<FeedEntryPage> QueryAsync(
        FeedEntryQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);

        string? search = string.IsNullOrWhiteSpace(query.SearchText)
            ? null
            : EscapeFtsPrefix(query.SearchText);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                e.id, e.feed_id, e.external_id, e.normalized_url, e.title, e.author,
                e.published_at, e.updated_at, e.summary, e.sanitized_content,
                e.enclosure_json, e.content_hash, e.fetched_at, e.has_full_content
            FROM feed_entries e
            LEFT JOIN feed_catalog f ON f.id=e.feed_id
            LEFT JOIN feed_categories c ON c.id=f.category_id
            WHERE ($feedId IS NULL OR e.feed_id=$feedId)
              AND ($categoryId IS NULL OR f.category_id=$categoryId)
              AND ($activeOnly = 0 OR (f.is_enabled = 1 AND (f.category_id IS NULL OR c.is_enabled = 1)))
              AND ($includeHidden = 1 OR NOT EXISTS (
                    SELECT 1
                    FROM user_entry_states private_hidden
                    WHERE private_hidden.entry_id=e.id
                      AND private_hidden.local_profile=$localProfile
                      AND private_hidden.is_hidden=1))
              AND ($publishedFrom IS NULL OR julianday(COALESCE(e.published_at, e.updated_at, e.fetched_at)) >= julianday($publishedFrom))
              AND ($publishedBefore IS NULL OR julianday(COALESCE(e.published_at, e.updated_at, e.fetched_at)) < julianday($publishedBefore))
              AND (
                    $readFilter = 0
                    OR ($readFilter = 1 AND NOT EXISTS (
                        SELECT 1
                        FROM user_entry_states private_read
                        WHERE private_read.entry_id=e.id
                          AND private_read.local_profile=$localProfile
                          AND private_read.is_read=1))
                    OR ($readFilter = 2 AND EXISTS (
                        SELECT 1
                        FROM user_entry_states private_read
                        WHERE private_read.entry_id=e.id
                          AND private_read.local_profile=$localProfile
                          AND private_read.is_read=1)))
              AND ($favoritesOnly = 0
                   OR EXISTS (
                        SELECT 1
                        FROM favorites private_favorite
                        WHERE private_favorite.entity_type='feed_entry'
                          AND private_favorite.entity_id=e.id)
                   OR EXISTS (
                        SELECT 1
                        FROM user_entry_states private_star
                        WHERE private_star.entry_id=e.id
                          AND private_star.local_profile=$localProfile
                          AND private_star.is_starred=1))
              AND ($tagId IS NULL OR EXISTS (
                    SELECT 1
                    FROM entity_tags private_tag
                    WHERE private_tag.entity_type='feed_entry'
                      AND private_tag.entity_id=e.id
                      AND private_tag.tag_id=$tagId))
              AND ($search IS NULL OR e.id IN (
                    SELECT entity_id
                    FROM content_fts
                    WHERE content_fts MATCH $search AND entity_type='feed_entry'))
            ORDER BY julianday(COALESCE(e.published_at, e.updated_at, e.fetched_at)) DESC, e.id
            LIMIT $limitPlusOne OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$feedId", (object?)query.FeedId ?? DBNull.Value);
        command.Parameters.AddWithValue("$categoryId", (object?)query.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$activeOnly", query.ActiveOnly ? 1 : 0);
        command.Parameters.AddWithValue("$includeHidden", query.IncludeHidden ? 1 : 0);
        command.Parameters.AddWithValue("$publishedFrom", FormatNullableTimestamp(query.PublishedFrom));
        command.Parameters.AddWithValue("$publishedBefore", FormatNullableTimestamp(query.PublishedBefore));
        command.Parameters.AddWithValue("$readFilter", (int)query.ReadFilter);
        command.Parameters.AddWithValue("$favoritesOnly", query.FavoritesOnly ? 1 : 0);
        command.Parameters.AddWithValue("$tagId", (object?)query.TagId ?? DBNull.Value);
        command.Parameters.AddWithValue("$localProfile", query.LocalProfile);
        command.Parameters.AddWithValue("$search", (object?)search ?? DBNull.Value);
        command.Parameters.AddWithValue("$limitPlusOne", query.Limit + 1);
        command.Parameters.AddWithValue("$offset", query.Offset);

        var items = new List<FeedEntry>(query.Limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadEntry(reader));
        }
        bool hasMore = items.Count > query.Limit;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, query.Offset, hasMore);
    }

    public async Task<int> DeleteExpiredUnprotectedAsync(
        DateTimeOffset cutoff,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            CREATE TEMP TABLE IF NOT EXISTS retention_entry_ids(
                id TEXT PRIMARY KEY
            ) WITHOUT ROWID;
            DELETE FROM retention_entry_ids;
            INSERT INTO retention_entry_ids(id)
            SELECT e.id
            FROM feed_entries e
            WHERE {FeedRetentionSql.CandidateWhereClause}
            ORDER BY
                julianday(COALESCE(
                    e.updated_at,
                    e.published_at,
                    e.fetched_at)),
                e.id
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoff));
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM retention_entry_ids;";
        int candidateCount = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (candidateCount == 0)
        {
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        command.CommandText = """
            DELETE FROM feed_automation_runs
            WHERE entry_id IN (SELECT id FROM retention_entry_ids);
            DELETE FROM feed_media_deliveries
            WHERE entry_id IN (SELECT id FROM retention_entry_ids);
            DELETE FROM entry_assets
            WHERE entry_id IN (SELECT id FROM retention_entry_ids);
            DELETE FROM feed_entries
            WHERE id IN (SELECT id FROM retention_entry_ids);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return candidateCount;
    }

    private static async Task UpsertEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedEntry entry,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO feed_entries(
                id, feed_id, external_id, normalized_url, title, author, published_at,
                updated_at, summary, sanitized_content, enclosure_json, content_hash, fetched_at,
                has_full_content)
            VALUES(
                $id, $feedId, $externalId, $normalizedUrl, $title, $author, $publishedAt,
                $updatedAt, $summary, $content, $enclosures, $contentHash, $fetchedAt,
                $hasFullContent)
            ON CONFLICT(feed_id, external_id) DO UPDATE SET
                normalized_url=excluded.normalized_url,
                title=excluded.title,
                author=excluded.author,
                published_at=excluded.published_at,
                updated_at=excluded.updated_at,
                summary=excluded.summary,
                sanitized_content=excluded.sanitized_content,
                enclosure_json=excluded.enclosure_json,
                content_hash=excluded.content_hash,
                fetched_at=excluded.fetched_at,
                has_full_content=excluded.has_full_content;
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$feedId", entry.FeedId);
        command.Parameters.AddWithValue("$externalId", entry.ExternalId);
        command.Parameters.AddWithValue("$normalizedUrl", (object?)entry.NormalizedUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$author", (object?)entry.Author ?? DBNull.Value);
        command.Parameters.AddWithValue("$publishedAt", FormatNullableTimestamp(entry.PublishedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatNullableTimestamp(entry.UpdatedAt));
        command.Parameters.AddWithValue("$summary", entry.Summary);
        command.Parameters.AddWithValue("$content", entry.SanitizedContent);
        command.Parameters.AddWithValue("$enclosures", JsonSerializer.Serialize(entry.Enclosures, JsonOptions));
        command.Parameters.AddWithValue("$contentHash", entry.ContentHash);
        command.Parameters.AddWithValue("$fetchedAt", FormatTimestamp(entry.FetchedAt));
        command.Parameters.AddWithValue("$hasFullContent", entry.HasFullContent);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FeedEntry ReadEntry(SqliteDataReader reader)
    {
        IReadOnlyList<FeedEnclosure> enclosures;
        try
        {
            enclosures = JsonSerializer.Deserialize<List<FeedEnclosure>>(reader.GetString(10), JsonOptions)
                ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored Feed enclosure data is invalid.", exception);
        }
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            ReadNullableTimestamp(reader, 6),
            ReadNullableTimestamp(reader, 7),
            reader.GetString(8),
            reader.GetString(9),
            [],
            enclosures,
            reader.GetString(11),
            ReadTimestamp(reader, 12),
            reader.GetBoolean(13));
    }

    private static void ValidateEntries(string feedId, IReadOnlyList<FeedEntry> entries)
    {
        ValidateOptionalGuid(feedId, nameof(feedId), required: true);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > 2000)
            throw new ArgumentOutOfRangeException(nameof(entries));
        if (entries.Any(entry => entry is null || !string.Equals(entry.FeedId, feedId, StringComparison.Ordinal)))
            throw new ArgumentException("All entries must belong to the requested Feed.", nameof(entries));
    }

    private static void ValidateQuery(FeedEntryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.SearchText?.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(query));
        ValidateOptionalGuid(query.FeedId, nameof(query.FeedId), required: false);
        ValidateOptionalGuid(query.CategoryId, nameof(query.CategoryId), required: false);
        ValidateOptionalIdentifier(query.TagId, nameof(query.TagId));
        ValidateProfile(query.LocalProfile);
        if (!Enum.IsDefined(query.ReadFilter)
            || query.Offset is < 0 or > 1_000_000
            || query.Limit is < 1 or > 200
            || (query.PublishedFrom is not null
                && query.PublishedBefore is not null
                && query.PublishedFrom >= query.PublishedBefore))
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }
    }

    private static void ValidateOptionalGuid(string? value, string parameterName, bool required)
    {
        if (value is null && !required) return;
        if (!Guid.TryParseExact(value, "D", out _))
            throw new ArgumentException("Identifier must be a canonical GUID.", parameterName);
    }

    private static void ValidateOptionalIdentifier(string? value, string parameterName)
    {
        if (value is null) return;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("Identifier is invalid.", parameterName);
        }
    }

    private static void ValidateProfile(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("Local profile is invalid.", nameof(value));
        }
    }

    private static string EscapeFtsPrefix(string value)
    {
        string normalized = value.Trim().Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{normalized}\"*";
    }

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
