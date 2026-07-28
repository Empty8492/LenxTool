using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedSmartViewAdminService
{
    Task<FeedSmartViewSnapshot> GetAllAsync(
        CancellationToken cancellationToken);

    Task<FeedSmartViewMutationResult> CreateAsync(
        FeedSmartViewInput input,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<FeedSmartViewMutationResult> UpdateAsync(
        string viewId,
        FeedSmartViewInput input,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<FeedSmartViewMutationResult> DeleteAsync(
        string viewId,
        long expectedVersion,
        CancellationToken cancellationToken);
}
