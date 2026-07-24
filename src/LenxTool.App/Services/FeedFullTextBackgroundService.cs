using LenxTool.Core.Contracts;
using LenxTool.Infrastructure.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LenxTool.App.Services;

internal sealed partial class FeedFullTextBackgroundService(
    IFeedFullTextQueueService queue,
    FeedFullTextQueueOptions options,
    ILogger<FeedFullTextBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.InitialDelay > TimeSpan.Zero)
        {
            await Task.Delay(options.InitialDelay, stoppingToken).ConfigureAwait(false);
        }

        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                int attempted = await queue.ProcessBackgroundBatchAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (attempted > 0)
                {
                    LogBatchCompleted(logger, attempted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogBatchFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(1101, LogLevel.Debug, "Processed {AttemptedCount} full-text extraction jobs")]
    private static partial void LogBatchCompleted(ILogger logger, int attemptedCount);

    [LoggerMessage(1102, LogLevel.Warning, "The full-text extraction queue pass failed")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);
}
