namespace LenxTool.Core.Contracts;

public interface IFeedAutomationNotificationActionProcessor
{
    Task<int> ProcessBackgroundBatchAsync(
        CancellationToken cancellationToken);
}
