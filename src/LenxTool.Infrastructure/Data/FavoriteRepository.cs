using System.Globalization;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class FavoriteRepository(SqliteDatabase database) : IFavoriteRepository
{
    private const int MaximumEntityTypeLength = 32;
    private const int MaximumEntityIdLength = 128;
    private const int MaximumNoteLength = 4000;
    private const int MaximumTagNameLength = 80;
    private const int MaximumTagColorLength = 32;
    private const int MaximumTagsPerEntity = 50;
    private const int MaximumBatchEntityIds = 200;

    public async Task<int> GetCountAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM favorites;";
        long count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return checked((int)count);
    }

    public async Task<FavoriteItem?> GetAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        ValidateEntity(entityType, entityId);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, entity_type, entity_id, note, created_at
            FROM favorites
            WHERE entity_type=$entityType AND entity_id=$entityId;
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadFavorite(reader)
            : null;
    }

    public async Task<FavoriteItem> UpsertAsync(
        string entityType,
        string entityId,
        string note,
        CancellationToken cancellationToken)
    {
        ValidateEntity(entityType, entityId);
        ValidateNote(note);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO favorites(id, entity_type, entity_id, note, created_at)
            VALUES($id, $entityType, $entityId, $note, $createdAt)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET note=excluded.note;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        FavoriteItem item = await ReadFavoriteAsync(
            connection,
            transaction,
            entityType,
            entityId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("收藏写入后无法读取。");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task<bool> RemoveAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        ValidateEntity(entityType, entityId);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM favorites
            WHERE entity_type=$entityType AND entity_id=$entityId;
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyDictionary<string, FavoriteItem>> GetForEntitiesAsync(
        string entityType,
        IReadOnlyCollection<string> entityIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ValidateEntityType(entityType);
        string[] ids = NormalizeIds(entityIds);
        if (ids.Length == 0)
        {
            return new Dictionary<string, FavoriteItem>(StringComparer.Ordinal);
        }
        if (ids.Length > MaximumBatchEntityIds)
        {
            throw new ArgumentOutOfRangeException(nameof(entityIds));
        }

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        var parameters = new List<string>(ids.Length);
        for (int index = 0; index < ids.Length; index++)
        {
            string parameter = $"$entity{index}";
            parameters.Add(parameter);
            command.Parameters.AddWithValue(parameter, ids[index]);
        }
        command.Parameters.AddWithValue("$entityType", entityType);
        command.CommandText = $"""
            SELECT id, entity_type, entity_id, note, created_at
            FROM favorites
            WHERE entity_type=$entityType AND entity_id IN ({string.Join(", ", parameters)});
            """;

        var result = new Dictionary<string, FavoriteItem>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            FavoriteItem item = ReadFavorite(reader);
            result[item.EntityId] = item;
        }
        return result;
    }

    public async Task<TagItem> UpsertTagAsync(
        string name,
        string color,
        CancellationToken cancellationToken)
    {
        string normalizedName = NormalizeTagName(name);
        ValidateTagColor(color);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tags(id, name, color, created_at)
            VALUES($id, $name, $color, $createdAt)
            ON CONFLICT(name) DO UPDATE SET color=excluded.color;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$name", normalizedName);
        command.Parameters.AddWithValue("$color", color);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        TagItem item = await ReadTagAsync(
            connection,
            transaction,
            normalizedName,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("标签写入后无法读取。");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task<TagItem> AddTagAsync(
        string entityType,
        string entityId,
        string name,
        string color,
        CancellationToken cancellationToken)
    {
        ValidateEntity(entityType, entityId);
        string normalizedName = NormalizeTagName(name);
        ValidateTagColor(color);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tags(id, name, color, created_at)
            VALUES($id, $name, $color, $createdAt)
            ON CONFLICT(name) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$name", normalizedName);
        command.Parameters.AddWithValue("$color", color);
        command.Parameters.AddWithValue(
            "$createdAt",
            FormatTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        TagItem item = await ReadTagAsync(
            connection,
            transaction,
            normalizedName,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("标签写入后无法读取。");

        command.Parameters.Clear();
        command.CommandText = """
            INSERT OR IGNORE INTO entity_tags(entity_type, entity_id, tag_id)
            SELECT $entityType, $entityId, $tagId
            WHERE (
                SELECT COUNT(*)
                FROM entity_tags
                WHERE entity_type=$entityType AND entity_id=$entityId
            ) < $maximumTags;
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$tagId", item.Id);
        command.Parameters.AddWithValue("$maximumTags", MaximumTagsPerEntity);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM entity_tags
                WHERE entity_type=$entityType
                  AND entity_id=$entityId
                  AND tag_id=$tagId);
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$tagId", item.Id);
        bool attached =
            (long)(await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false))! == 1;
        if (!attached)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                "实体标签数量已达到上限。");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task<IReadOnlyList<TagItem>> GetTagsAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, color, created_at
            FROM tags
            ORDER BY name COLLATE NOCASE, id;
            """;
        var result = new List<TagItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadTag(reader));
        }
        return result;
    }

    public async Task<IReadOnlyList<TagItem>> GetTagsForEntityAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        ValidateEntity(entityType, entityId);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.name, t.color, t.created_at
            FROM tags t
            INNER JOIN entity_tags et ON et.tag_id=t.id
            WHERE et.entity_type=$entityType AND et.entity_id=$entityId
            ORDER BY t.name COLLATE NOCASE, t.id;
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        var result = new List<TagItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadTag(reader));
        }
        return result;
    }

    public async Task SetTagsAsync(
        string entityType,
        string entityId,
        IReadOnlyCollection<string> tagIds,
        CancellationToken cancellationToken)
    {
        ValidateEntity(entityType, entityId);
        ArgumentNullException.ThrowIfNull(tagIds);
        string[] ids = tagIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length > MaximumTagsPerEntity)
        {
            throw new ArgumentOutOfRangeException(nameof(tagIds));
        }

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Length > 0)
        {
            var parameters = new List<string>(ids.Length);
            await using SqliteCommand existingTags = connection.CreateCommand();
            existingTags.Transaction = transaction;
            for (int index = 0; index < ids.Length; index++)
            {
                string parameter = $"$tag{index}";
                parameters.Add(parameter);
                existingTags.Parameters.AddWithValue(parameter, ids[index]);
            }
            existingTags.CommandText =
                $"SELECT COUNT(*) FROM tags WHERE id IN ({string.Join(", ", parameters)});";
            long existingCount = (long)(await existingTags.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false))!;
            if (existingCount != ids.Length)
            {
                throw new ArgumentException("标签列表包含不存在的标签。", nameof(tagIds));
            }
        }

        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM entity_tags
                WHERE entity_type=$entityType AND entity_id=$entityId;
                """;
            delete.Parameters.AddWithValue("$entityType", entityType);
            delete.Parameters.AddWithValue("$entityId", entityId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (string tagId in ids)
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO entity_tags(entity_type, entity_id, tag_id)
                VALUES($entityType, $entityId, $tagId);
                """;
            insert.Parameters.AddWithValue("$entityType", entityType);
            insert.Parameters.AddWithValue("$entityId", entityId);
            insert.Parameters.AddWithValue("$tagId", tagId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteTagAsync(
        string tagId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(tagId, nameof(tagId), MaximumEntityIdLength);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tags WHERE id=$id;";
        command.Parameters.AddWithValue("$id", tagId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<FavoriteItem?> ReadFavoriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, entity_type, entity_id, note, created_at
            FROM favorites
            WHERE entity_type=$entityType AND entity_id=$entityId;
            """;
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadFavorite(reader)
            : null;
    }

    private static async Task<TagItem?> ReadTagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, name, color, created_at
            FROM tags
            WHERE name=$name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$name", name);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTag(reader)
            : null;
    }

    private static FavoriteItem ReadFavorite(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        ParseTimestamp(reader.GetString(4)));

    private static TagItem ReadTag(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        ParseTimestamp(reader.GetString(3)));

    private static string[] NormalizeIds(IReadOnlyCollection<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ValidateIdentifier(value, "entityId", MaximumEntityIdLength))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void ValidateEntity(string entityType, string entityId)
    {
        ValidateEntityType(entityType);
        ValidateIdentifier(entityId, nameof(entityId), MaximumEntityIdLength);
    }

    private static void ValidateEntityType(string value) =>
        ValidateIdentifier(value, nameof(value), MaximumEntityTypeLength);

    private static string ValidateIdentifier(
        string value,
        string parameterName,
        int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return value;
    }

    private static void ValidateNote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumNoteLength
            || value.Any(character =>
                char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static string NormalizeTagName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length is 0 or > MaximumTagNameLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return normalized;
    }

    private static void ValidateTagColor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumTagColorLength || value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
