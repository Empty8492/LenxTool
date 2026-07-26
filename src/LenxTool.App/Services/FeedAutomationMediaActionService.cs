using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public sealed class FeedAutomationMediaActionService :
    IFeedAutomationMediaActionService
{
    private readonly IFeedCatalogRepository _catalogRepository;
    private readonly IFeedEntryRepository _entryRepository;
    private readonly IFeedMediaDeliveryService _deliveryService;
    private readonly IMediaJobInbox _mediaJobInbox;

    public FeedAutomationMediaActionService(
        IFeedCatalogRepository catalogRepository,
        IFeedEntryRepository entryRepository,
        IFeedMediaDeliveryService deliveryService,
        IMediaJobInbox mediaJobInbox)
    {
        ArgumentNullException.ThrowIfNull(catalogRepository);
        ArgumentNullException.ThrowIfNull(entryRepository);
        ArgumentNullException.ThrowIfNull(deliveryService);
        ArgumentNullException.ThrowIfNull(mediaJobInbox);
        _catalogRepository = catalogRepository;
        _entryRepository = entryRepository;
        _deliveryService = deliveryService;
        _mediaJobInbox = mediaJobInbox;
    }

    public async Task<FeedAutomationMediaActionResult> ExecuteAsync(
        FeedAutomationActionLease action,
        CancellationToken cancellationToken)
    {
        ValidateAction(action);

        FeedEntry? entry = await _entryRepository
            .GetByIdAsync(action.EntryId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return FeedAutomationMediaActionResult.EntryMissing;
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
            return FeedAutomationMediaActionResult.FeedUnavailable;
        }

        FeedEnclosure? enclosure = entry.Enclosures.FirstOrDefault(
            candidate => IsSupportedMedia(candidate, entry.NormalizedUrl));
        if (enclosure is null)
        {
            return FeedAutomationMediaActionResult.NoSupportedMedia;
        }

        FeedMediaDeliveryRegistration registration =
            await _deliveryService.DeliverAsync(
            entry,
            enclosure,
            cancellationToken).ConfigureAwait(false);
        _mediaJobInbox.PublishQueued(registration.Job);
        return FeedAutomationMediaActionResult.Completed;
    }

    private static bool IsSupportedMedia(
        FeedEnclosure enclosure,
        string? baseUrl)
    {
        FeedAttachmentClassification attachment =
            FeedAttachmentClassifier.Classify(enclosure, baseUrl);
        return attachment.UrlStatus == FeedAttachmentUrlStatus.Allowed &&
            attachment.IsTypeVerified &&
            attachment.Kind is
                FeedAttachmentKind.Audio or FeedAttachmentKind.Video;
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
        if (action.Type != FeedAutomationActionType.SendToMedia ||
            action.Value is not null)
        {
            throw new ArgumentException(
                "The action is not a supported media action.",
                nameof(action));
        }
    }

    private static AppException CatalogUnavailable() =>
        new(
            new(
                AppErrorCode.ProviderUnavailable,
                "订阅目录暂不可用",
                "当前无法读取订阅目录，媒体自动化动作尚未执行。",
                "请等待目录同步完成后重试。",
                Provider: "本地订阅目录",
                IsRetryable: true));
}
