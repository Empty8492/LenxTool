using System.Windows;

namespace LenxTool.App.Services;

public sealed class WpfWindowsNotificationActivationTarget(
    IAppNotificationNavigationService navigation)
    : IWindowsNotificationActivationTarget
{
    public async Task OpenAsync(
        string notificationId,
        CancellationToken cancellationToken)
    {
        Application? application = Application.Current;
        if (application is null)
        {
            return;
        }

        await application.Dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Window? window = application.MainWindow;
            if (window is not null)
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }
                if (!window.IsVisible)
                {
                    window.Show();
                }
                window.Activate();
            }

            await navigation.OpenAsync(notificationId, cancellationToken)
                .ConfigureAwait(true);
        }).Task.Unwrap().ConfigureAwait(false);
    }
}
