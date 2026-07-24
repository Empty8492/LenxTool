using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedCatalogRepository(SqliteDatabase database) : IFeedCatalogRepository
{
    public async Task ReplaceAsync(
        FeedCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ValidateSnapshot(snapshot);

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        FeedCatalogState currentState = await ReadStateAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.State.Version < currentState.Version)
        {
            throw new InvalidOperationException(
                $"Catalog version cannot move backwards from {currentState.Version} to {snapshot.State.Version}.");
        }

        IReadOnlyList<FeedFetchState> fetchStates = await ReadFetchStatesAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);

        await ClearCatalogAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (FeedCategory category in snapshot.Categories)
        {
            await InsertCategoryAsync(connection, transaction, category, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (FeedCatalogItem feed in snapshot.Feeds)
        {
            await InsertFeedAsync(connection, transaction, feed, cancellationToken)
                .ConfigureAwait(false);
        }

        var retainedFeedIds = snapshot.Feeds
            .Select(feed => feed.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (FeedFetchState fetchState in fetchStates)
        {
            if (retainedFeedIds.Contains(fetchState.FeedId))
            {
                await InsertFetchStateAsync(connection, transaction, fetchState, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await UpdateStateAsync(connection, transaction, snapshot.State, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeedCatalogSnapshot?> GetCatalogAsync(
        FeedCatalogScope scope,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        FeedCatalogState storedState = await ReadStateAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (scope == FeedCatalogScope.All && storedState.Scope != FeedCatalogScope.All)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        IReadOnlyList<FeedCategory> categories = await ReadCategoriesAsync(
            connection,
            transaction,
            scope,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FeedCatalogItem> feeds = await ReadFeedsAsync(
            connection,
            transaction,
            scope,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new(storedState with { Scope = scope }, categories, feeds);
    }

    public async Task<FeedCatalogState> GetStateAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadStateAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSynchronizedAsync(
        long expectedVersion,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE feed_catalog_state
            SET last_synced_at=$lastSyncedAt
            WHERE singleton_id=1 AND catalog_version=$expectedVersion;
            """;
        command.Parameters.AddWithValue("$lastSyncedAt", FormatTimestamp(synchronizedAt));
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows != 1)
        {
            throw new InvalidOperationException(
                "The feed catalog version changed while marking synchronization complete.");
        }
    }

    private static async Task ClearCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM feed_catalog; DELETE FROM feed_categories;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCategoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedCategory category,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO feed_categories(
                id, name, name_norm, sort_order, is_enabled, version, created_at, updated_at)
            VALUES(
                $id, $name, $normalizedName, $sortOrder, $isEnabled, $version, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", category.Id);
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$normalizedName", category.NormalizedName);
        command.Parameters.AddWithValue("$sortOrder", category.SortOrder);
        command.Parameters.AddWithValue("$isEnabled", category.IsEnabled);
        command.Parameters.AddWithValue("$version", category.Version);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(category.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(category.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertFeedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedCatalogItem feed,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO feed_catalog(
                id, original_url, normalized_url, display_name, site_url, category_id,
                view_kind, refresh_interval_minutes, sort_order, is_enabled, version,
                created_at, updated_at, full_text_policy)
            VALUES(
                $id, $originalUrl, $normalizedUrl, $displayName, $siteUrl, $categoryId,
                $viewKind, $refreshIntervalMinutes, $sortOrder, $isEnabled, $version,
                $createdAt, $updatedAt, $fullTextPolicy);
            """;
        command.Parameters.AddWithValue("$id", feed.Id);
        command.Parameters.AddWithValue("$originalUrl", feed.OriginalUrl);
        command.Parameters.AddWithValue("$normalizedUrl", feed.NormalizedUrl);
        command.Parameters.AddWithValue("$displayName", feed.DisplayName);
        command.Parameters.AddWithValue("$siteUrl", (object?)feed.SiteUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$categoryId", (object?)feed.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$viewKind", ToStorageValue(feed.ViewKind));
        command.Parameters.AddWithValue("$refreshIntervalMinutes", feed.RefreshIntervalMinutes);
        command.Parameters.AddWithValue("$sortOrder", feed.SortOrder);
        command.Parameters.AddWithValue("$isEnabled", feed.IsEnabled);
        command.Parameters.AddWithValue("$version", feed.Version);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(feed.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(feed.UpdatedAt));
        command.Parameters.AddWithValue("$fullTextPolicy", ToStorageValue(feed.FullTextPolicy));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertFetchStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedFetchState state,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO feed_fetch_state(
                feed_id, etag, last_modified, next_fetch_at, last_success_at, last_failure_at,
                consecutive_failures, error_code, updated_at)
            VALUES(
                $feedId, $etag, $lastModified, $nextFetchAt, $lastSuccessAt, $lastFailureAt,
                $consecutiveFailures, $errorCode, $updatedAt);
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
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedCatalogState state,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE feed_catalog_state
            SET catalog_version=$version,
                scope=$scope,
                generated_at=$generatedAt,
                last_synced_at=$lastSyncedAt
            WHERE singleton_id=1;
            """;
        command.Parameters.AddWithValue("$version", state.Version);
        command.Parameters.AddWithValue("$scope", ToStorageValue(state.Scope));
        command.Parameters.AddWithValue("$generatedAt", FormatNullableTimestamp(state.GeneratedAt));
        command.Parameters.AddWithValue("$lastSyncedAt", FormatNullableTimestamp(state.LastSyncedAt));
        int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows != 1)
        {
            throw new InvalidOperationException("The feed catalog state row is missing.");
        }
    }

    private static async Task<FeedCatalogState> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT catalog_version, scope, generated_at, last_synced_at
            FROM feed_catalog_state
            WHERE singleton_id=1;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The feed catalog state row is missing.");
        }

        return new(
            reader.GetInt64(0),
            ParseScope(reader.GetString(1)),
            ReadNullableTimestamp(reader, 2),
            ReadNullableTimestamp(reader, 3));
    }

    private static async Task<IReadOnlyList<FeedCategory>> ReadCategoriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedCatalogScope scope,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, name, name_norm, sort_order, is_enabled, version, created_at, updated_at
            FROM feed_categories
            WHERE $includeDisabled=1 OR is_enabled=1
            ORDER BY sort_order, name, id;
            """;
        command.Parameters.AddWithValue("$includeDisabled", scope == FeedCatalogScope.All);
        var categories = new List<FeedCategory>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            categories.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetBoolean(4),
                reader.GetInt64(5),
                ReadTimestamp(reader, 6),
                ReadTimestamp(reader, 7)));
        }

        return categories;
    }

    private static async Task<IReadOnlyList<FeedCatalogItem>> ReadFeedsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedCatalogScope scope,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT f.id, f.original_url, f.normalized_url, f.display_name, f.site_url,
                   f.category_id, f.view_kind, f.refresh_interval_minutes, f.sort_order,
                   f.is_enabled, f.version, f.created_at, f.updated_at, f.full_text_policy
            FROM feed_catalog f
            LEFT JOIN feed_categories c ON c.id=f.category_id
            WHERE $includeDisabled=1
               OR (f.is_enabled=1 AND (f.category_id IS NULL OR c.is_enabled=1))
            ORDER BY
                CASE WHEN f.category_id IS NULL THEN 1 ELSE 0 END,
                c.sort_order,
                c.name,
                c.id,
                f.sort_order,
                f.display_name,
                f.id;
            """;
        command.Parameters.AddWithValue("$includeDisabled", scope == FeedCatalogScope.All);
        var feeds = new List<FeedCatalogItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            feeds.Add(new(
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
                ReadTimestamp(reader, 12),
                ParseFullTextPolicy(reader.GetString(13))));
        }

        return feeds;
    }

    private static async Task<IReadOnlyList<FeedFetchState>> ReadFetchStatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT feed_id, etag, last_modified, next_fetch_at, last_success_at, last_failure_at,
                   consecutive_failures, error_code, updated_at
            FROM feed_fetch_state;
            """;
        var states = new List<FeedFetchState>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            states.Add(new(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ReadNullableTimestamp(reader, 3),
                ReadNullableTimestamp(reader, 4),
                ReadNullableTimestamp(reader, 5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                ReadTimestamp(reader, 8)));
        }

        return states;
    }

    private static void ValidateSnapshot(FeedCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.State);
        ArgumentNullException.ThrowIfNull(snapshot.Categories);
        ArgumentNullException.ThrowIfNull(snapshot.Feeds);
        ValidateScope(snapshot.State.Scope);
        if (snapshot.State.Version < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Catalog version must be non-negative.");
        }

        if (snapshot.State.GeneratedAt is null || snapshot.State.LastSyncedAt is null)
        {
            throw new ArgumentException(
                "A replacement snapshot must include generated and last-synchronized timestamps.",
                nameof(snapshot));
        }
        if (snapshot.Feeds.Any(feed => !Enum.IsDefined(feed.FullTextPolicy)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshot),
                "Feed full-text policy is invalid.");
        }
    }

    private static void ValidateScope(FeedCatalogScope scope)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
    }

    private static string ToStorageValue(FeedCatalogScope scope) => scope switch
    {
        FeedCatalogScope.Active => "ACTIVE",
        FeedCatalogScope.All => "ALL",
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    private static FeedCatalogScope ParseScope(string value) => value switch
    {
        "ACTIVE" => FeedCatalogScope.Active,
        "ALL" => FeedCatalogScope.All,
        _ => throw new InvalidDataException($"Unknown feed catalog scope '{value}'.")
    };

    private static string ToStorageValue(FeedViewKind viewKind) => viewKind switch
    {
        FeedViewKind.Article => "ARTICLE",
        FeedViewKind.Picture => "PICTURE",
        FeedViewKind.Audio => "AUDIO",
        FeedViewKind.Video => "VIDEO",
        FeedViewKind.Notification => "NOTIFICATION",
        _ => throw new ArgumentOutOfRangeException(nameof(viewKind))
    };

    private static FeedViewKind ParseViewKind(string value) => value switch
    {
        "ARTICLE" => FeedViewKind.Article,
        "PICTURE" => FeedViewKind.Picture,
        "AUDIO" => FeedViewKind.Audio,
        "VIDEO" => FeedViewKind.Video,
        "NOTIFICATION" => FeedViewKind.Notification,
        _ => throw new InvalidDataException($"Unknown feed view kind '{value}'.")
    };

    private static string ToStorageValue(FeedFullTextPolicy policy) => policy switch
    {
        FeedFullTextPolicy.None => "NONE",
        FeedFullTextPolicy.OnOpen => "ON_OPEN",
        FeedFullTextPolicy.Background => "BACKGROUND",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    private static FeedFullTextPolicy ParseFullTextPolicy(string value) => value switch
    {
        "NONE" => FeedFullTextPolicy.None,
        "ON_OPEN" => FeedFullTextPolicy.OnOpen,
        "BACKGROUND" => FeedFullTextPolicy.Background,
        _ => throw new InvalidDataException($"Unknown feed full-text policy '{value}'.")
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

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
