using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class EntryStateRepository(SqliteDatabase database) : IEntryStateRepository
{
    private const int MaximumNoteLength = 4000;
    private const int MaximumProfileLength = 64;

    public async Task<IReadOnlyDictionary<string, EntryState>> GetAsync(
        IReadOnlyCollection<string> entryIds,
        string localProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        ValidateProfile(localProfile);
        string[] ids = entryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) return new Dictionary<string, EntryState>(StringComparer.Ordinal);
        if (ids.Length > 200) throw new ArgumentOutOfRangeException(nameof(entryIds));

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        var parameters = new List<string>(ids.Length);
        for (int index = 0; index < ids.Length; index++)
        {
            string parameter = $"$entry{index}";
            parameters.Add(parameter);
            command.Parameters.AddWithValue(parameter, ids[index]);
        }
        command.Parameters.AddWithValue("$profile", localProfile);
        command.CommandText = $"""
            SELECT entry_id, local_profile, is_read, is_starred, progress, note, updated_at
            FROM user_entry_states
            WHERE local_profile=$profile AND entry_id IN ({string.Join(", ", parameters)});
            """;

        var states = new Dictionary<string, EntryState>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            states[reader.GetString(0)] = ReadState(reader);
        }
        return states;
    }

    public async Task<EntryState> PatchAsync(
        string entryId,
        string localProfile,
        EntryStatePatch patch,
        CancellationToken cancellationToken)
    {
        ValidateEntryId(entryId);
        ValidateProfile(localProfile);
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Progress is { } progress && (double.IsNaN(progress) || double.IsInfinity(progress) || progress is < 0 or > 100))
            throw new ArgumentOutOfRangeException(nameof(patch));
        if (patch.Note is { Length: > MaximumNoteLength })
            throw new ArgumentOutOfRangeException(nameof(patch));

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        EntryState? current = await ReadSingleAsync(
            connection,
            transaction,
            entryId,
            localProfile,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        EntryState updated = new(
            entryId,
            localProfile,
            patch.IsRead ?? current?.IsRead ?? false,
            patch.IsStarred ?? current?.IsStarred ?? false,
            patch.Progress ?? current?.Progress ?? 0,
            patch.Note ?? current?.Note ?? string.Empty,
            now);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_entry_states(
                entry_id, local_profile, is_read, is_starred, progress, note, updated_at)
            VALUES($entryId, $profile, $isRead, $isStarred, $progress, $note, $updatedAt)
            ON CONFLICT(entry_id, local_profile) DO UPDATE SET
                is_read=excluded.is_read,
                is_starred=excluded.is_starred,
                progress=excluded.progress,
                note=excluded.note,
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$entryId", updated.EntryId);
        command.Parameters.AddWithValue("$profile", updated.LocalProfile);
        command.Parameters.AddWithValue("$isRead", updated.IsRead);
        command.Parameters.AddWithValue("$isStarred", updated.IsStarred);
        command.Parameters.AddWithValue("$progress", updated.Progress);
        command.Parameters.AddWithValue("$note", updated.Note);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(updated.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static async Task<EntryState?> ReadSingleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entryId,
        string localProfile,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT entry_id, local_profile, is_read, is_starred, progress, note, updated_at
            FROM user_entry_states
            WHERE entry_id=$entryId AND local_profile=$profile;
            """;
        command.Parameters.AddWithValue("$entryId", entryId);
        command.Parameters.AddWithValue("$profile", localProfile);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadState(reader)
            : null;
    }

    private static EntryState ReadState(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2) != 0,
        reader.GetInt64(3) != 0,
        reader.GetDouble(4),
        reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));

    private static void ValidateEntryId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static void ValidateProfile(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumProfileLength) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
