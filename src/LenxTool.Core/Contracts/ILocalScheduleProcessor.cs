namespace LenxTool.Core.Contracts;

public interface ILocalScheduleProcessor
{
    Task<int> ProcessBackgroundBatchAsync(
        CancellationToken cancellationToken);
}
