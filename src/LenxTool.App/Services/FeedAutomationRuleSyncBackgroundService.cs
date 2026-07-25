using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Infrastructure.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LenxTool.App.Services;

internal sealed partial class FeedAutomationRuleSyncBackgroundService(
    IAccountSessionService accountSession,
    IFeedAutomationRuleSyncService synchronization,
    FeedAutomationRuleSyncOptions options,
    ILogger<FeedAutomationRuleSyncBackgroundService> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        ValidateOptions(options);
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

                TimeSpan delay = options.SynchronizationInterval;
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
                    delay = options.RetryInterval;
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
        AccountSessionChangedEventArgs eventArgs) =>
        Signal();

    private void Signal()
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

    private static void ValidateOptions(
        FeedAutomationRuleSyncOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SynchronizationInterval <= TimeSpan.Zero
            || value.RetryInterval <= TimeSpan.Zero
            || value.RetryInterval > value.SynchronizationInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    [LoggerMessage(
        1401,
        LogLevel.Warning,
        "The ACTIVE Feed automation rule synchronization failed")]
    private static partial void LogSynchronizationFailed(
        ILogger logger,
        Exception exception);
}
