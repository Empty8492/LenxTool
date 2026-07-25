namespace LenxTool.Core.Contracts;

public interface IFeedAutomationAiActionProcessor
{
    Task<int> ProcessBackgroundBatchAsync(
        CancellationToken cancellationToken);
}
