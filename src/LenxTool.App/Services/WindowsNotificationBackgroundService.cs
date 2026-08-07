using Microsoft.Extensions.Hosting;

namespace LenxTool.App.Services;

internal sealed class WindowsNotificationBackgroundService(
    WindowsNotificationService notifications) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        notifications.RunAsync(stoppingToken);
}
