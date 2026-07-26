namespace LenxTool.Core.Models;

public sealed record AppNotification(
    string Id,
    string EntryId,
    string FeedId,
    string RuleId,
    int RuleVersion,
    string Title,
    string SourceLabel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt)
{
    public bool IsRead => ReadAt is not null;
}

public sealed record AppNotificationRegistration(
    AppNotification Notification,
    bool Created);
