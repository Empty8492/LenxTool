using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LenxTool.App.Services;

internal sealed partial class LocalScheduleBackgroundService(
    ILocalScheduleProcessor processor,
    LocalScheduleProcessorOptions options,
    TimeProvider timeProvider,
    ILogger<LocalScheduleBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            options.PollInterval,
            timeProvider);
        do
        {
            try
            {
                int attempted = await processor.ProcessBackgroundBatchAsync(
                    stoppingToken).ConfigureAwait(false);
                if (attempted > 0)
                {
                    LogWindowProcessed(logger);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogPassFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken)
                   .ConfigureAwait(false));
    }

    [LoggerMessage(
        1601,
        LogLevel.Debug,
        "Processed one local schedule window")]
    private static partial void LogWindowProcessed(ILogger logger);

    [LoggerMessage(
        1602,
        LogLevel.Warning,
        "The local schedule pass failed")]
    private static partial void LogPassFailed(
        ILogger logger,
        Exception exception);
}
