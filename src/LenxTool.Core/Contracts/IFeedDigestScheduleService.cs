using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedDigestScheduleService
{
    Task<FeedDigestScheduleState> GetAsync(
        FeedDigestPeriod period,
        CancellationToken cancellationToken);

    Task<FeedDigestScheduleState> SaveAsync(
        FeedDigestScheduleConfiguration configuration,
        CancellationToken cancellationToken);
}
