namespace LenxTool.Core.Contracts;

public interface IFeedAutomationMediaActionProcessor
{
    Task<int> ProcessBackgroundBatchAsync(
        CancellationToken cancellationToken);
}
