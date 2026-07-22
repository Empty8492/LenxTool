using System.Globalization;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedEntryWriter(SqliteDatabase database) : IFeedEntryWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task UpsertAsync(
        string feedId,
        IReadOnlyList<FeedEntry> entries,
        CancellationToken cancellationToken)
    {
        Validate(feedId, entries);
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
                updated_at, summary, sanitized_content, enclosure_json, content_hash, fetched_at)
            VALUES(
                $id, $feedId, $externalId, $normalizedUrl, $title, $author, $publishedAt,
                $updatedAt, $summary, $content, $enclosures, $contentHash, $fetchedAt)
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
                fetched_at=excluded.fetched_at;
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
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(string feedId, IReadOnlyList<FeedEntry> entries)
    {
        if (!Guid.TryParseExact(feedId, "D", out _))
            throw new ArgumentException("Feed ID must be a canonical GUID.", nameof(feedId));
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > 2000)
            throw new ArgumentOutOfRangeException(nameof(entries));
        if (entries.Any(entry => entry is null || !string.Equals(entry.FeedId, feedId, StringComparison.Ordinal)))
            throw new ArgumentException("All entries must belong to the requested Feed.", nameof(entries));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullableTimestamp(DateTimeOffset? value) =>
        value is null ? DBNull.Value : FormatTimestamp(value.Value);
}
