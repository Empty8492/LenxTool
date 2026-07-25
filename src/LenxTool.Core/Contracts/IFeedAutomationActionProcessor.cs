namespace LenxTool.Core.Contracts;

public interface IFeedAutomationActionProcessor
{
    Task<int> ProcessBackgroundBatchAsync(
        CancellationToken cancellationToken);
}
