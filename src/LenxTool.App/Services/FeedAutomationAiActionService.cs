using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public sealed class FeedAutomationAiActionService :
    IFeedAutomationAiActionService
{
    private readonly IFeedAiAutomationJobRepository _jobs;
    private readonly IFeedCatalogRepository _catalogRepository;
    private readonly IFeedEntryRepository _entryRepository;
    private readonly IFeedAiSummaryService _summaryService;
    private readonly IFeedAiTranslationService _translationService;
    private readonly TimeProvider _timeProvider;

    public FeedAutomationAiActionService(
        IFeedAiAutomationJobRepository jobs,
        IFeedCatalogRepository catalogRepository,
        IFeedEntryRepository entryRepository,
        IFeedAiSummaryService summaryService,
        IFeedAiTranslationService translationService,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(catalogRepository);
        ArgumentNullException.ThrowIfNull(entryRepository);
        ArgumentNullException.ThrowIfNull(summaryService);
        ArgumentNullException.ThrowIfNull(translationService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _jobs = jobs;
        _catalogRepository = catalogRepository;
        _entryRepository = entryRepository;
        _summaryService = summaryService;
        _translationService = translationService;
        _timeProvider = timeProvider;
    }

    public async Task<FeedAutomationAiActionResult> ExecuteAsync(
        FeedAutomationActionLease action,
        CancellationToken cancellationToken)
    {
        ValidateAction(action);

        FeedEntry? entry = await _entryRepository
            .GetByIdAsync(action.EntryId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return FeedAutomationAiActionResult.EntryMissing;
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
            candidate => candidate.IsEnabled
                && string.Equals(
                    candidate.Id,
                    entry.FeedId,
                    StringComparison.Ordinal));
        if (feed is null
            || !IsCategoryAvailable(catalog, feed.CategoryId))
        {
            return FeedAutomationAiActionResult.FeedUnavailable;
        }

        ResolvedFeedAiPolicy policy =
            FeedAiPolicyResolver.Resolve(catalog, feed);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateOnly usageDate = DateOnly.FromDateTime(now.UtcDateTime);
        bool reserved = await _jobs.TryReserveDailyEntryAsync(
            usageDate,
            entry.FeedId,
            entry.Id,
            policy.DailyEntryLimit,
            now,
            cancellationToken).ConfigureAwait(false);
        if (!reserved)
        {
            throw DailyEntryLimitReached(usageDate, now);
        }

        FeedAiAutomationTaskType taskType =
            action.Type == FeedAutomationActionType.GenerateSummary
                ? FeedAiAutomationTaskType.Summary
                : FeedAiAutomationTaskType.Translation;
        await FeedAiTaskExecution.ExecuteAsync(
            entry,
            taskType,
            action.Value ?? "und",
            _summaryService,
            _translationService,
            cancellationToken).ConfigureAwait(false);
        return FeedAutomationAiActionResult.Completed;
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
            category.IsEnabled
            && string.Equals(
                category.Id,
                categoryId,
                StringComparison.Ordinal));
    }

    private static void ValidateAction(
        FeedAutomationActionLease action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Type == FeedAutomationActionType.GenerateSummary)
        {
            if (action.Value is not null)
            {
                throw new ArgumentException(
                    "Summary actions cannot contain a value.",
                    nameof(action));
            }
            return;
        }

        if (action.Type != FeedAutomationActionType.Translate
            || action.Value is not ("zh-Hans" or "en" or "ja" or "ko"))
        {
            throw new ArgumentException(
                "The action is not a supported AI action.",
                nameof(action));
        }
    }

    private static AppException CatalogUnavailable() =>
        new(
            new(
                AppErrorCode.ProviderUnavailable,
                "订阅目录暂不可用",
                "当前无法读取订阅目录，AI 自动化动作尚未执行。",
                "请等待目录同步完成后重试。",
                Provider: "本地订阅目录",
                IsRetryable: true));

    private static AppException DailyEntryLimitReached(
        DateOnly usageDate,
        DateTimeOffset now)
    {
        DateTimeOffset nextDay = new(
            usageDate.AddDays(1).ToDateTime(new TimeOnly(0, 1)),
            TimeSpan.Zero);
        TimeSpan retryAfter = nextDay - now;
        if (retryAfter <= TimeSpan.Zero)
        {
            retryAfter = TimeSpan.FromMinutes(1);
        }

        return new(
            new(
                AppErrorCode.ProviderRateLimited,
                "今日 AI 自动处理额度已用完",
                "该订阅今日允许处理的资讯数量已达到上限。",
                "任务会在下一个 UTC 日期自动重试。",
                Provider: "Feed AI 策略",
                RetryAfter: retryAfter,
                IsRetryable: true));
    }
}
