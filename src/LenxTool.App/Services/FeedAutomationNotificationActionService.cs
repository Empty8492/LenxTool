using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public sealed class FeedAutomationNotificationActionService
    : IFeedAutomationNotificationActionService
{
    private readonly IFeedCatalogRepository _catalogRepository;
    private readonly IFeedEntryRepository _entryRepository;
    private readonly IAppNotificationRepository _notifications;
    private readonly IAppNotificationInbox _inbox;
    private readonly TimeProvider _timeProvider;

    public FeedAutomationNotificationActionService(
        IFeedCatalogRepository catalogRepository,
        IFeedEntryRepository entryRepository,
        IAppNotificationRepository notifications,
        IAppNotificationInbox inbox,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(catalogRepository);
        ArgumentNullException.ThrowIfNull(entryRepository);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _catalogRepository = catalogRepository;
        _entryRepository = entryRepository;
        _notifications = notifications;
        _inbox = inbox;
        _timeProvider = timeProvider;
    }

    public async Task<FeedAutomationNotificationActionResult> ExecuteAsync(
        FeedAutomationActionLease action,
        CancellationToken cancellationToken)
    {
        ValidateAction(action);

        FeedEntry? entry = await _entryRepository
            .GetByIdAsync(action.EntryId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return FeedAutomationNotificationActionResult.EntryMissing;
        }

        FeedCatalogState state = await _catalogRepository
            .GetStateAsync(cancellationToken)
            .ConfigureAwait(false);
        FeedCatalogSnapshot? catalog = await _catalogRepository
            .GetCatalogAsync(state.Scope, cancellationToken)
            .ConfigureAwait(false);
        if (catalog is null)
        {
            throw CatalogUnavailable();
        }

        FeedCatalogItem? feed = catalog.Feeds.FirstOrDefault(
            candidate => candidate.IsEnabled &&
                string.Equals(
                    candidate.Id,
                    entry.FeedId,
                    StringComparison.Ordinal));
        if (feed is null ||
            !IsCategoryAvailable(catalog, feed.CategoryId))
        {
            return FeedAutomationNotificationActionResult.FeedUnavailable;
        }

        var notification = new AppNotification(
            action.IdempotencyKey,
            entry.Id,
            entry.FeedId,
            action.RuleId,
            action.RuleVersion,
            NormalizeLabel(entry.Title, 1_024, "无标题资讯"),
            NormalizeLabel(feed.DisplayName, 160, "未知订阅"),
            _timeProvider.GetUtcNow(),
            ReadAt: null,
            AppNotificationKind.ContentMatch,
            AppNotificationTargetKind.FeedEntry,
            entry.Id);
        AppNotificationRegistration registration =
            await _notifications.RegisterAsync(
                notification,
                cancellationToken).ConfigureAwait(false);
        if (registration.Created)
        {
            _inbox.Publish(registration.Notification);
        }
        return FeedAutomationNotificationActionResult.Completed;
    }

    private static bool IsCategoryAvailable(
        FeedCatalogSnapshot catalog,
        string? categoryId)
    {
        if (categoryId is null)
        {
            return true;
        }

        return catalog.Categories.Any(category =>
            category.IsEnabled &&
            string.Equals(
                category.Id,
                categoryId,
                StringComparison.Ordinal));
    }

    private static void ValidateAction(
        FeedAutomationActionLease action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Type != FeedAutomationActionType.Notify ||
            action.Value is not null)
        {
            throw new ArgumentException(
                "The action is not a supported notification action.",
                nameof(action));
        }
    }

    private static string NormalizeLabel(
        string? value,
        int maximumLength,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var result = new StringBuilder(
            Math.Min(value.Length, maximumLength));
        bool needsSpace = false;
        foreach (char character in value.Normalize(
                     NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(character) ||
                char.IsControl(character))
            {
                needsSpace = result.Length > 0;
                continue;
            }

            if (needsSpace && result.Length < maximumLength)
            {
                result.Append(' ');
            }
            needsSpace = false;
            if (result.Length >= maximumLength)
            {
                break;
            }
            result.Append(character);
        }

        if (result.Length > 0 &&
            char.IsHighSurrogate(result[^1]))
        {
            result.Length--;
        }
        return result.Length == 0
            ? fallback
            : result.ToString();
    }

    private static AppException CatalogUnavailable() =>
        new(
            new(
                AppErrorCode.ProviderUnavailable,
                "订阅目录暂不可用",
                "当前无法读取订阅目录，通知自动化动作尚未执行。",
                "请等待目录同步完成后重试。",
                Provider: "本地订阅目录",
                IsRetryable: true));
}
