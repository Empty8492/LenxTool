using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LenxTool.App.Services;

internal sealed partial class FeedSmartViewSyncBackgroundService(
    IAccountSessionService accountSession,
    IFeedSmartViewSyncService synchronization,
    ILogger<FeedSmartViewSyncBackgroundService> logger)
    : BackgroundService
{
    private static readonly TimeSpan SynchronizationInterval =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryInterval =
        TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        accountSession.SessionChanged += OnSessionChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!accountSession.Current.IsAuthenticated)
                {
                    await _wakeSignal.WaitAsync(stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                TimeSpan delay = SynchronizationInterval;
                try
                {
                    await synchronization.SyncAsync(stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    delay = RetryInterval;
                    LogSynchronizationFailed(logger, exception);
                }
                await _wakeSignal.WaitAsync(delay, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            accountSession.SessionChanged -= OnSessionChanged;
        }
    }

    private void OnSessionChanged(
        object? sender,
        AccountSessionChangedEventArgs eventArgs)
    {
        try
        {
            if (_wakeSignal.CurrentCount == 0)
            {
                _wakeSignal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
    }

    [LoggerMessage(
        1501,
        LogLevel.Warning,
        "The ACTIVE Feed smart view synchronization failed")]
    private static partial void LogSynchronizationFailed(
        ILogger logger,
        Exception exception);
}
