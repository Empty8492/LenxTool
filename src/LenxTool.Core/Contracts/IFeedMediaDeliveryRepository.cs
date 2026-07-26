using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedMediaDeliveryRepository
{
    Task<FeedMediaDeliveryRegistration> CreateOrGetQueuedAsync(
        FeedMediaDelivery delivery,
        MediaJob queuedJob,
        CancellationToken cancellationToken);

    Task<FeedMediaDeliveryRegistration?> GetAsync(
        string entryId,
        string sourceUrl,
        CancellationToken cancellationToken);
}
