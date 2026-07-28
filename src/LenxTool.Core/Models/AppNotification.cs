namespace LenxTool.Core.Models;

public enum AppNotificationKind
{
    ContentMatch,
    SystemHealth,
    TaskCompleted
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
    AppNotificationKind Kind = AppNotificationKind.ContentMatch)
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
    int? RuleVersion = null);
