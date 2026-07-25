using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LenxTool.App.Services;

internal sealed partial class FeedAutomationAiActionBackgroundService(
    IFeedAutomationAiActionProcessor processor,
    FeedAutomationActionProcessorOptions options,
    ILogger<FeedAutomationAiActionBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (options.InitialDelay > TimeSpan.Zero)
        {
            await Task.Delay(options.InitialDelay, stoppingToken)
                .ConfigureAwait(false);
        }

        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                int attempted =
                    await processor.ProcessBackgroundBatchAsync(stoppingToken)
                        .ConfigureAwait(false);
                if (attempted > 0)
                {
                    LogBatchCompleted(logger, attempted);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogBatchFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken)
                   .ConfigureAwait(false));
    }

    [LoggerMessage(
        1501,
        LogLevel.Debug,
        "Processed {AttemptedCount} AI Feed automation actions")]
    private static partial void LogBatchCompleted(
        ILogger logger,
        int attemptedCount);

    [LoggerMessage(
        1502,
        LogLevel.Warning,
        "The AI Feed automation action queue pass failed")]
    private static partial void LogBatchFailed(
        ILogger logger,
        Exception exception);
}
