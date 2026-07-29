using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LenxTool.App.Services;

internal sealed partial class EntryExportQueueBackgroundService(
    IEntryExportQueueProcessor processor,
    EntryExportQueueOptions options,
    ILogger<EntryExportQueueBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                int attempted = await processor.ProcessBackgroundBatchAsync(
                    stoppingToken).ConfigureAwait(false);
                if (attempted > 0)
                {
                    LogTaskProcessed(logger);
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
        1501,
        LogLevel.Debug,
        "Processed one entry export task")]
    private static partial void LogTaskProcessed(ILogger logger);

    [LoggerMessage(
        1502,
        LogLevel.Warning,
        "The entry export queue pass failed")]
    private static partial void LogPassFailed(
        ILogger logger,
        Exception exception);
}
