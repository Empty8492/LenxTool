namespace LenxTool.Core.Models;

public enum AppNotificationKind
{
    ContentMatch,
    SystemHealth,
    TaskCompleted
}

public enum AppNotificationTargetKind
{
    None,
    FeedEntry,
    AiReport
}

public static class AppNotificationTargetPolicy
{
    public static bool IsValid(
        AppNotificationTargetKind kind,
        string? targetId) =>
        kind switch
        {
            AppNotificationTargetKind.None => targetId is null,
            AppNotificationTargetKind.FeedEntry or
                AppNotificationTargetKind.AiReport =>
                IsSafeEntityId(targetId),
            _ => false
        };

    public static bool IsSafeEntityId(string? value) =>
        value is { Length: >= 1 and <= 512 } &&
        value.All(character =>
            character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or ':');
}

public sealed record AppNotification(
    string Id,
    string EntryId,
    string FeedId,
    string RuleId,
    int RuleVersion,
    string Title,
    string SourceLabel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    AppNotificationKind Kind = AppNotificationKind.ContentMatch,
    AppNotificationTargetKind TargetKind = AppNotificationTargetKind.None,
    string? TargetId = null)
{
    public bool IsRead => ReadAt is not null;

    public string KindLabel => Kind switch
    {
        AppNotificationKind.ContentMatch => "内容命中",
        AppNotificationKind.SystemHealth => "系统健康",
        AppNotificationKind.TaskCompleted => "任务完成",
        _ => "通知"
    };
}

public sealed record AppNotificationRegistration(
    AppNotification Notification,
    bool Created);

public sealed record AppNotificationDraft(
    AppNotificationKind Kind,
    string DedupeKey,
    string EntryId,
    string FeedId,
    string Title,
    string SourceLabel,
    string? RuleId = null,
    int? RuleVersion = null,
    AppNotificationTargetKind TargetKind = AppNotificationTargetKind.None,
    string? TargetId = null);
