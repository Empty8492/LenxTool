using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedMediaDeliveryService
{
    Task<FeedMediaDeliveryRegistration> DeliverAsync(
        FeedEntry entry,
        FeedEnclosure enclosure,
        CancellationToken cancellationToken);
}
