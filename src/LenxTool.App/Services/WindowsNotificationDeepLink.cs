using System.IO;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public static class WindowsNotificationActivation
{
    private const string NotificationIdKey = "notification_id";
    private const int NotificationIdLength = 64;

    public static IReadOnlyDictionary<string, string> CreateArguments(
        string notificationId)
    {
        if (!IsValidNotificationId(notificationId))
        {
            throw new ArgumentOutOfRangeException(nameof(notificationId));
        }
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NotificationIdKey] = notificationId
        };
    }

    public static bool TryParse(
        IReadOnlyDictionary<string, string>? arguments,
        out string? notificationId)
    {
        notificationId = null;
        if (arguments is null || arguments.Count != 1 ||
            !arguments.TryGetValue(NotificationIdKey, out string? value) ||
            !IsValidNotificationId(value))
        {
            return false;
        }
        notificationId = value;
        return true;
    }

    public static bool IsValidNotificationId(string? value) =>
        value is { Length: NotificationIdLength } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class WindowsNotificationDeepLink
{
    public static AppNavigationRequest For(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return notification.TargetKind switch
        {
            AppNotificationTargetKind.None
                when notification.TargetId is null &&
                     WindowsNotificationActivation.IsValidNotificationId(
                         notification.Id) =>
                new(
                    "notifications",
                    "app_notification",
                    notification.Id),
            AppNotificationTargetKind.FeedEntry =>
                new(
                    "news",
                    "feed_entry",
                    RequireEntityId(
                        notification.TargetKind,
                        notification.TargetId)),
            AppNotificationTargetKind.AiReport =>
                new(
                    "ai-reports",
                    "ai_report",
                    RequireEntityId(
                        notification.TargetKind,
                        notification.TargetId)),
            _ => throw new InvalidDataException(
                "通知目标不是受支持的应用内实体。")
        };
    }

    private static string RequireEntityId(
        AppNotificationTargetKind kind,
        string? value)
    {
        if (!AppNotificationTargetPolicy.IsValid(kind, value))
        {
            throw new InvalidDataException(
                "通知实体 ID 不是安全的本地标识。");
        }
        return value!;
    }
}
