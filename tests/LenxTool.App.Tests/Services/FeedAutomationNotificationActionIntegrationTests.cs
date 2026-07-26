using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.SystemServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationNotificationActionIntegrationTests
    : IDisposable
{
    private const string FeedId =
        "30000000-0000-4000-8000-000000000801";
    private const string CategoryId =
        "20000000-0000-4000-8000-000000000801";
    private const string RuleId =
        "40000000-0000-4000-8000-000000000801";
    private const string EntryId = "entry-notification-e2e";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 22, 0, 0, TimeSpan.Zero);
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "Lenx Tools notification integration tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DurableNotifyActionUpdatesInboxAndRestoresReadState()
    {
        var paths = new AppPaths(_testRoot);
        using (var database = new SqliteDatabase(
                   paths,
                   NullLogger<SqliteDatabase>.Instance))
        {
            await database.InitializeAsync(CancellationToken.None);
            await SeedCatalogAndEntryAsync(database);
            await new FeedAutomationRunRepository(database).StageAsync(
                Plan(),
                Now,
                CancellationToken.None);
            var notifications = new AppNotificationRepository(database);
            var inbox = new AppNotificationInbox();
            var center = new NotificationCenterViewModel(
                notifications,
                inbox,
                new FixedTimeProvider(Now.AddMinutes(1)));
            await center.InitializeAsync(CancellationToken.None);
            var actions = new FeedAutomationNotificationActionService(
                new FeedCatalogRepository(database),
                new FeedEntryRepository(database),
                notifications,
                inbox,
                new FixedTimeProvider(Now));
            var processor = new FeedAutomationNotificationActionProcessor(
                new FeedAutomationActionQueueRepository(database),
                actions,
                new FixedTimeProvider(Now),
                FeedAutomationActionProcessorOptions.Default with
                {
                    BatchSize = 1,
                    InitialDelay = TimeSpan.Zero
                });

            Assert.Equal(
                1,
                await processor.ProcessBackgroundBatchAsync(
                    CancellationToken.None));

            FeedAutomationActionRun run = Assert.Single(
                (await new FeedAutomationRunRepository(database).GetAsync(
                    EntryId,
                    CancellationToken.None)).ActionRuns);
            Assert.Equal(FeedAutomationActionRunStatus.Succeeded, run.Status);
            AppNotification notification = Assert.Single(
                await notifications.GetRecentAsync(
                    20,
                    CancellationToken.None));
            Assert.Equal(run.IdempotencyKey, notification.Id);
            Assert.Equal("端到端通知", notification.Title);
            Assert.Equal("AI 资讯", notification.SourceLabel);
            Assert.Equal(notification, Assert.Single(center.Items));
            Assert.Equal(1, center.UnreadCount);

            await center.MarkReadCommand.ExecuteAsync(notification);

            Assert.Equal(0, center.UnreadCount);
            Assert.True(Assert.Single(center.Items).IsRead);
        }

        using var reopened = new SqliteDatabase(
            paths,
            NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync(CancellationToken.None);
        AppNotification restored = Assert.Single(
            await new AppNotificationRepository(reopened).GetRecentAsync(
                20,
                CancellationToken.None));
        Assert.Equal(Now.AddMinutes(1), restored.ReadAt);
    }

    private static async Task SeedCatalogAndEntryAsync(
        SqliteDatabase database)
    {
        var catalog = new FeedCatalogSnapshot(
            new(1, FeedCatalogScope.All, Now, Now),
            [
                new(
                    CategoryId,
                    "Tech",
                    "tech",
                    0,
                    true,
                    1,
                    Now,
                    Now)
            ],
            [
                new(
                    FeedId,
                    "https://news.example/feed.xml",
                    "https://news.example/feed.xml",
                    "AI 资讯",
                    null,
                    CategoryId,
                    FeedViewKind.Article,
                    60,
                    0,
                    true,
                    1,
                    Now,
                    Now)
            ],
            FeedAiPolicy.SafeDefaults);
        await new FeedCatalogRepository(database).ReplaceAsync(
            catalog,
            CancellationToken.None);
        var entry = new FeedEntry(
            EntryId,
            FeedId,
            "external-notification-e2e",
            "https://news.example/articles/notification-e2e",
            "端到端通知",
            null,
            Now,
            Now,
            "摘要不进入通知",
            "正文不进入通知",
            [],
            [],
            new string('d', 64),
            Now);
        await new FeedEntryRepository(database).UpsertAsync(
            FeedId,
            [entry],
            CancellationToken.None);
    }

    private static FeedAutomationPlan Plan() => new(
        EntryId,
        [
            new(
                RuleId,
                1,
                FeedAutomationRuleEvaluationOutcome.Matched)
        ],
        [
            new(
                RuleId,
                1,
                100,
                0,
                FeedAutomationActionType.Notify,
                10,
                null,
                FeedAutomationActionDisposition.Planned,
                FeedAutomationActionSuppressionReason.None,
                null,
                null,
                null)
        ]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
