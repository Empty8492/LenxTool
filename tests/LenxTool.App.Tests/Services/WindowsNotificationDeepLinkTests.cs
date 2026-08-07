using LenxTool.App.Services;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class WindowsNotificationDeepLinkTests
{
    [Fact]
    public void ActivationPayloadContainsOnlyTheLocalNotificationId()
    {
        IReadOnlyDictionary<string, string> arguments =
            WindowsNotificationActivation.CreateArguments(NotificationId);

        KeyValuePair<string, string> argument = Assert.Single(arguments);
        Assert.Equal("notification_id", argument.Key);
        Assert.Equal(NotificationId, argument.Value);
        Assert.True(WindowsNotificationActivation.TryParse(
            arguments,
            out string? parsed));
        Assert.Equal(NotificationId, parsed);
    }

    [Theory]
    [MemberData(nameof(RejectedArguments))]
    public void ActivationParserRejectsUrlsUnknownKeysAndMalformedIds(
        IReadOnlyDictionary<string, string> arguments)
    {
        Assert.False(WindowsNotificationActivation.TryParse(
            arguments,
            out _));
    }

    [Theory]
    [InlineData(
        AppNotificationTargetKind.FeedEntry,
        "news",
        "feed_entry")]
    [InlineData(
        AppNotificationTargetKind.AiReport,
        "ai-reports",
        "ai_report")]
    public void TrustedPersistedTargetsMapToClosedApplicationRoutes(
        AppNotificationTargetKind targetKind,
        string expectedRoute,
        string expectedEntityType)
    {
        AppNotification notification = CreateNotification(
            targetKind,
            EntityId);

        AppNavigationRequest request =
            WindowsNotificationDeepLink.For(notification);

        Assert.Equal(expectedRoute, request.RouteId);
        Assert.Equal(expectedEntityType, request.EntityType);
        Assert.Equal(EntityId, request.EntityId);
    }

    [Fact]
    public void NotificationWithoutEntityTargetMapsOnlyToTheInbox()
    {
        AppNotification notification = CreateNotification(
            AppNotificationTargetKind.None,
            targetId: null);

        AppNavigationRequest request =
            WindowsNotificationDeepLink.For(notification);

        Assert.Equal("notifications", request.RouteId);
        Assert.Equal("app_notification", request.EntityType);
        Assert.Equal(NotificationId, request.EntityId);
    }

    [Fact]
    public void InvalidPersistedTargetFailsClosed()
    {
        AppNotification invalid = CreateNotification(
            AppNotificationTargetKind.FeedEntry,
            "https://example.com/article");

        Assert.Throws<InvalidDataException>(
            () => WindowsNotificationDeepLink.For(invalid));
    }

    [Fact]
    public void InboxTargetStillRequiresCanonicalNotificationId()
    {
        AppNotification invalid = CreateNotification(
            AppNotificationTargetKind.None,
            targetId: null) with
        {
            Id = "not-a-notification-id"
        };

        Assert.Throws<InvalidDataException>(
            () => WindowsNotificationDeepLink.For(invalid));
    }

    public static TheoryData<IReadOnlyDictionary<string, string>>
        RejectedArguments => new()
        {
            Arguments("https://example.com/article"),
            Arguments("file://C:/secret.txt"),
            Arguments("lenxtool://news/1"),
            Arguments("not-hex"),
            Arguments(NotificationId.ToUpperInvariant()),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["notification_id"] = NotificationId,
                ["uri"] = "https://example.com"
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
        };

    private const string NotificationId =
        "59e8f62691b35bc028c5e32939b793a8a469498d0758fe4b0974f01c03d3031a";
    private const string EntityId =
        "7f0a7aa7b7f2ee754c1f6337becc09d87885d985dfcba6e71bb69bee9c535b46";

    private static Dictionary<string, string> Arguments(string value) =>
        new(StringComparer.Ordinal)
        {
            ["notification_id"] = value
        };

    private static AppNotification CreateNotification(
        AppNotificationTargetKind targetKind,
        string? targetId) =>
        new(
            NotificationId,
            EntityId,
            "feed-1",
            Guid.Empty.ToString("D"),
            1,
            "标题",
            "来源",
            new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero),
            ReadAt: null,
            AppNotificationKind.ContentMatch,
            targetKind,
            targetId);
}
