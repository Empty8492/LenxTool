using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Data;

public sealed class AppNotificationRepository(SqliteDatabase database)
    : IAppNotificationRepository
{
    public async Task<AppNotificationRegistration> RegisterAsync(
        AppNotification notification,
        CancellationToken cancellationToken)
    {
        Validate(notification);

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        AppNotification? existing = await FindAsync(
            connection,
            transaction,
            notification.Id,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(existing, Created: false);
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_notifications(
                id, entry_id, feed_id, rule_id, rule_version,
                title, source_label, created_at, read_at, kind,
                target_kind, target_id)
            VALUES(
                $id, $entryId, $feedId, $ruleId, $ruleVersion,
                $title, $sourceLabel, $createdAt, $readAt, $kind,
                $targetKind, $targetId);
            """;
        AddNotificationParameters(command, notification);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(notification, Created: true);
    }

    public async Task<IReadOnlyList<AppNotification>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM app_notifications
            ORDER BY created_at DESC, id
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppNotification?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM app_notifications
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        IReadOnlyList<AppNotification> notifications =
            await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return notifications.SingleOrDefault();
    }

    public async Task<int> GetUnreadCountAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM app_notifications WHERE read_at IS NULL;";
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false))!);
    }

    public async Task<bool> MarkReadAsync(
        string id,
        DateTimeOffset readAt,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        ValidateTimestamp(readAt, nameof(readAt));

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE app_notifications
            SET read_at=$readAt
            WHERE id=$id AND read_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue(
            "$readAt",
            readAt.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<int> MarkAllReadAsync(
        DateTimeOffset readAt,
        CancellationToken cancellationToken)
    {
        ValidateTimestamp(readAt, nameof(readAt));

        await using SqliteConnection connection =
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE app_notifications
            SET read_at=$readAt
            WHERE read_at IS NULL;
            """;
        command.Parameters.AddWithValue(
            "$readAt",
            readAt.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<AppNotification?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM app_notifications
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        IReadOnlyList<AppNotification> notifications =
            await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        return notifications.SingleOrDefault();
    }

    private static async Task<IReadOnlyList<AppNotification>> ReadAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var notifications = new List<AppNotification>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            notifications.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                DateTimeOffset.Parse(
                    reader.GetString(7),
                    CultureInfo.InvariantCulture),
                reader.IsDBNull(8)
                    ? null
                    : DateTimeOffset.Parse(
                        reader.GetString(8),
                        CultureInfo.InvariantCulture),
                ParseKind(reader.GetString(9)),
                ParseTargetKind(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return notifications;
    }

    private static void AddNotificationParameters(
        SqliteCommand command,
        AppNotification notification)
    {
        command.Parameters.AddWithValue("$id", notification.Id);
        command.Parameters.AddWithValue("$entryId", notification.EntryId);
        command.Parameters.AddWithValue("$feedId", notification.FeedId);
        command.Parameters.AddWithValue("$ruleId", notification.RuleId);
        command.Parameters.AddWithValue("$ruleVersion", notification.RuleVersion);
        command.Parameters.AddWithValue("$title", notification.Title);
        command.Parameters.AddWithValue("$sourceLabel", notification.SourceLabel);
        command.Parameters.AddWithValue(
            "$createdAt",
            notification.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$readAt",
            notification.ReadAt is null
                ? DBNull.Value
                : notification.ReadAt.Value.ToString(
                    "O",
                    CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$kind",
            StoreKind(notification.Kind));
        command.Parameters.AddWithValue(
            "$targetKind",
            StoreTargetKind(notification.TargetKind));
        command.Parameters.AddWithValue(
            "$targetId",
            notification.TargetId is null
                ? DBNull.Value
                : notification.TargetId);
    }

    private static void Validate(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ValidateId(notification.Id);
        ValidateText(notification.EntryId, nameof(notification.EntryId), 512);
        ValidateText(notification.FeedId, nameof(notification.FeedId), 512);
        if (!Guid.TryParseExact(notification.RuleId, "D", out _))
        {
            throw new ArgumentException(
                "通知规则 ID 必须是规范 GUID。",
                nameof(notification));
        }
        if (notification.RuleVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(notification));
        }
        ValidateText(notification.Title, nameof(notification.Title), 1_024);
        ValidateText(
            notification.SourceLabel,
            nameof(notification.SourceLabel),
            160);
        if (!Enum.IsDefined(notification.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(notification));
        }
        if (!AppNotificationTargetPolicy.IsValid(
                notification.TargetKind,
                notification.TargetId))
        {
            throw new ArgumentException(
                "通知目标必须是封闭类型与安全本地实体 ID 的有效组合。",
                nameof(notification));
        }
        ValidateTimestamp(notification.CreatedAt, nameof(notification.CreatedAt));
        if (notification.ReadAt is { } readAt)
        {
            ValidateTimestamp(readAt, nameof(notification.ReadAt));
            if (readAt < notification.CreatedAt)
            {
                throw new ArgumentOutOfRangeException(nameof(notification));
            }
        }
    }

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length != 64 || id.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "通知 ID 必须是 64 位十六进制幂等键。",
                nameof(id));
        }
    }

    private static void ValidateText(
        string value,
        string parameterName,
        int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "通知时间必须是 UTC。",
                parameterName);
        }
    }

    private const string SelectColumns = """
        id, entry_id, feed_id, rule_id, rule_version,
        title, source_label, created_at, read_at, kind,
        target_kind, target_id
        """;

    private static string StoreKind(AppNotificationKind kind) =>
        kind switch
        {
            AppNotificationKind.ContentMatch => "CONTENT_MATCH",
            AppNotificationKind.SystemHealth => "SYSTEM_HEALTH",
            AppNotificationKind.TaskCompleted => "TASK_COMPLETED",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static AppNotificationKind ParseKind(string value) =>
        value switch
        {
            "CONTENT_MATCH" => AppNotificationKind.ContentMatch,
            "SYSTEM_HEALTH" => AppNotificationKind.SystemHealth,
            "TASK_COMPLETED" => AppNotificationKind.TaskCompleted,
            _ => throw new InvalidDataException(
                "通知类别不受支持。")
        };

    private static string StoreTargetKind(
        AppNotificationTargetKind kind) =>
        kind switch
        {
            AppNotificationTargetKind.None => "NONE",
            AppNotificationTargetKind.FeedEntry => "FEED_ENTRY",
            AppNotificationTargetKind.AiReport => "AI_REPORT",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static AppNotificationTargetKind ParseTargetKind(string value) =>
        value switch
        {
            "NONE" => AppNotificationTargetKind.None,
            "FEED_ENTRY" => AppNotificationTargetKind.FeedEntry,
            "AI_REPORT" => AppNotificationTargetKind.AiReport,
            _ => throw new InvalidDataException(
                "通知目标类别不受支持。")
        };
}
