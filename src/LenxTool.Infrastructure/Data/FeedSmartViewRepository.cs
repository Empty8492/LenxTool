using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FeedSmartViewRepository(SqliteDatabase database)
    : IFeedSmartViewRepository
{
    public async Task<FeedSmartViewSnapshot> GetAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        return await ReadAsync(
            connection,
            transaction: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceAsync(
        FeedSmartViewSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ValidateSnapshot(snapshot);
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        FeedSmartViewSnapshot current = await ReadAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        bool initialEmptySnapshot =
            snapshot.ViewSetVersion == 0
            && current.ViewSetVersion == 0
            && current.GeneratedAt is null
            && current.LastSyncedAt is null
            && current.Views.Count == 0;
        if (!initialEmptySnapshot &&
            snapshot.ViewSetVersion <= current.ViewSetVersion)
        {
            throw new InvalidOperationException(
                "智能视图快照版本必须单调增加。");
        }

        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM feed_smart_views;";
            await delete.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        foreach (FeedSmartView view in snapshot.Views)
        {
            FeedSmartView normalized =
                FeedSmartViewValidator.ValidateAndNormalize(view);
            await InsertAsync(
                connection,
                transaction,
                normalized,
                cancellationToken).ConfigureAwait(false);
        }
        await using (SqliteCommand state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                UPDATE feed_smart_view_state
                SET view_set_version=$version,
                    generated_at=$generatedAt,
                    last_synced_at=$lastSyncedAt
                WHERE singleton_id=1;
                """;
            state.Parameters.AddWithValue(
                "$version",
                snapshot.ViewSetVersion);
            state.Parameters.AddWithValue(
                "$generatedAt",
                Format(snapshot.GeneratedAt));
            state.Parameters.AddWithValue(
                "$lastSyncedAt",
                Format(snapshot.LastSyncedAt));
            await state.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> MarkSynchronizedAsync(
        long expectedVersion,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken)
    {
        ValidateVersion(expectedVersion);
        ValidateTimestamp(synchronizedAt, nameof(synchronizedAt));
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE feed_smart_view_state
            SET last_synced_at=$synchronizedAt
            WHERE singleton_id=1 AND view_set_version=$expectedVersion;
            """;
        command.Parameters.AddWithValue(
            "$synchronizedAt",
            synchronizedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$expectedVersion",
            expectedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    private static async Task<FeedSmartViewSnapshot> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        long version;
        DateTimeOffset? generatedAt;
        DateTimeOffset? lastSyncedAt;
        await using (SqliteCommand state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                SELECT view_set_version, generated_at, last_synced_at
                FROM feed_smart_view_state
                WHERE singleton_id=1;
                """;
            await using SqliteDataReader reader =
                await state.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "本地智能视图状态缺失。");
            }
            version = reader.GetInt64(0);
            generatedAt = ReadTimestamp(reader, 1);
            lastSyncedAt = ReadTimestamp(reader, 2);
        }

        var views = new List<FeedSmartView>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, version, name, sort_order, is_enabled,
                       feed_id, category_id, view_kind, read_filter,
                       favorites_only, search_text, published_within_days
                FROM feed_smart_views
                ORDER BY sort_order, name, id;
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                FeedSmartView view = new(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt64(4) == 1,
                    new(
                        reader.IsDBNull(5)
                            ? null
                            : reader.GetString(5),
                        reader.IsDBNull(6)
                            ? null
                            : reader.GetString(6),
                        reader.IsDBNull(7)
                            ? null
                            : ParseViewKind(reader.GetString(7)),
                        ParseReadFilter(reader.GetString(8)),
                        reader.GetInt64(9) == 1,
                        reader.IsDBNull(10)
                            ? null
                            : reader.GetString(10),
                        reader.IsDBNull(11)
                            ? null
                            : reader.GetInt32(11)));
                views.Add(
                    FeedSmartViewValidator.ValidateAndNormalize(view));
            }
        }
        FeedSmartViewSnapshot snapshot = new(
            version,
            FeedSmartViewScope.Active,
            generatedAt,
            lastSyncedAt,
            views);
        ValidateSnapshot(snapshot);
        return snapshot;
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedSmartView view,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO feed_smart_views(
                id, version, name, sort_order, is_enabled,
                feed_id, category_id, view_kind, read_filter,
                favorites_only, search_text, published_within_days)
            VALUES(
                $id, $version, $name, $sortOrder, 1,
                $feedId, $categoryId, $viewKind, $readFilter,
                $favoritesOnly, $searchText, $publishedWithinDays);
            """;
        command.Parameters.AddWithValue("$id", view.Id);
        command.Parameters.AddWithValue("$version", view.Version);
        command.Parameters.AddWithValue("$name", view.Name);
        command.Parameters.AddWithValue("$sortOrder", view.SortOrder);
        command.Parameters.AddWithValue(
            "$feedId",
            (object?)view.Filter.FeedId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$categoryId",
            (object?)view.Filter.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$viewKind",
            view.Filter.ViewKind is { } viewKind
                ? StoreViewKind(viewKind)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$readFilter",
            StoreReadFilter(view.Filter.ReadFilter));
        command.Parameters.AddWithValue(
            "$favoritesOnly",
            view.Filter.FavoritesOnly ? 1 : 0);
        command.Parameters.AddWithValue(
            "$searchText",
            (object?)view.Filter.SearchText ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$publishedWithinDays",
            (object?)view.Filter.PublishedWithinDays ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateSnapshot(FeedSmartViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Views);
        ValidateVersion(snapshot.ViewSetVersion);
        if (snapshot.Scope != FeedSmartViewScope.Active ||
            snapshot.Views.Count > FeedSmartViewValidator.MaximumViews)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        }
        if (snapshot.ViewSetVersion == 0 &&
            (snapshot.GeneratedAt is not null ||
                snapshot.Views.Count != 0))
        {
            throw new ArgumentException(
                "零版本智能视图快照必须为空。",
                nameof(snapshot));
        }
        if (snapshot.ViewSetVersion > 0 && snapshot.GeneratedAt is null)
        {
            throw new ArgumentException(
                "非空版本必须包含生成时间。",
                nameof(snapshot));
        }
        if (snapshot.GeneratedAt is { } generatedAt)
        {
            ValidateTimestamp(generatedAt, nameof(snapshot.GeneratedAt));
        }
        if (snapshot.LastSyncedAt is { } lastSyncedAt)
        {
            ValidateTimestamp(
                lastSyncedAt,
                nameof(snapshot.LastSyncedAt));
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (FeedSmartView view in snapshot.Views)
        {
            FeedSmartView normalized =
                FeedSmartViewValidator.ValidateAndNormalize(view);
            if (!normalized.IsEnabled || !ids.Add(normalized.Id))
            {
                throw new ArgumentException(
                    "ACTIVE 智能视图必须启用且 ID 唯一。",
                    nameof(snapshot));
            }
        }
    }

    private static void ValidateVersion(long version)
    {
        if (version is < 0 or > 9_007_199_254_740_991)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }
    }

    private static void ValidateTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "智能视图时间必须是 UTC。",
                parameterName);
        }
    }

    private static object Format(DateTimeOffset? value) =>
        value is null
            ? DBNull.Value
            : value.Value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadTimestamp(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture);

    private static string StoreViewKind(EntryViewKind kind) =>
        kind.ToString().ToUpperInvariant();

    private static EntryViewKind ParseViewKind(string value) =>
        value switch
        {
            "ARTICLE" => EntryViewKind.Article,
            "PICTURE" => EntryViewKind.Picture,
            "AUDIO" => EntryViewKind.Audio,
            "VIDEO" => EntryViewKind.Video,
            "NOTIFICATION" => EntryViewKind.Notification,
            _ => throw new InvalidDataException(
                "本地智能视图内容类别无效。")
        };

    private static string StoreReadFilter(
        FeedEntryReadFilter filter) =>
        filter switch
        {
            FeedEntryReadFilter.All => "ALL",
            FeedEntryReadFilter.Unread => "UNREAD",
            FeedEntryReadFilter.Read => "READ",
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };

    private static FeedEntryReadFilter ParseReadFilter(string value) =>
        value switch
        {
            "ALL" => FeedEntryReadFilter.All,
            "UNREAD" => FeedEntryReadFilter.Unread,
            "READ" => FeedEntryReadFilter.Read,
            _ => throw new InvalidDataException(
                "本地智能视图已读筛选无效。")
        };
}
