using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace LenxTool.App.Services;

public sealed class WindowsAppSdkNotificationAdapter
    : IWindowsNotificationAdapter
{
    private readonly object _gate = new();
    private readonly IWindowsAppRuntimeBootstrap _bootstrap;
    private AppNotificationManager? _manager;
    private bool _registered;
    private bool _registrationFailed;
    private bool _runtimeInitialized;

    public WindowsAppSdkNotificationAdapter()
        : this(new WindowsAppRuntimeBootstrap())
    {
    }

    internal WindowsAppSdkNotificationAdapter(
        IWindowsAppRuntimeBootstrap bootstrap)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(
            nameof(bootstrap));
    }

    public event EventHandler<WindowsNotificationActivatedEventArgs>?
        Activated;

    public WindowsNotificationAvailability Availability
    {
        get
        {
            lock (_gate)
            {
                if (_registrationFailed)
                {
                    return WindowsNotificationAvailability.RegistrationFailed;
                }
                if (!_registered || _manager is null)
                {
                    return WindowsNotificationAvailability.Unsupported;
                }
                return Map(_manager.Setting);
            }
        }
    }

    public void Register()
    {
        lock (_gate)
        {
            if (_registered || _registrationFailed)
            {
                return;
            }
        }

        bool runtimeInitialized;
        try
        {
            runtimeInitialized = _bootstrap.TryInitialize(out _);
        }
        catch
        {
            runtimeInitialized = false;
        }
        if (!runtimeInitialized)
        {
            lock (_gate)
            {
                _registrationFailed = true;
            }
            return;
        }
        lock (_gate)
        {
            _runtimeInitialized = true;
        }

        AppNotificationManager? manager = null;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                ReleaseRuntime();
                return;
            }

            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            lock (_gate)
            {
                _manager = manager;
                _registered = true;
            }
        }
        catch
        {
            if (manager is not null)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
            }
            lock (_gate)
            {
                _registrationFailed = true;
            }
            ReleaseRuntime();
            throw;
        }
    }

    public void Show(WindowsSystemNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        AppNotificationManager manager;
        lock (_gate)
        {
            manager = _manager ?? throw new InvalidOperationException(
                "Windows notification manager is not registered.");
        }

        var builder = new AppNotificationBuilder();
        foreach (KeyValuePair<string, string> argument in
                 notification.Arguments)
        {
            builder.AddArgument(argument.Key, argument.Value);
        }
        builder.AddText(notification.Title);
        builder.AddText(notification.Body);
        manager.Show(builder.BuildNotification());
    }

    public void Unregister()
    {
        AppNotificationManager? manager;
        lock (_gate)
        {
            manager = _manager;
            _manager = null;
            _registered = false;
        }

        try
        {
            if (manager is not null)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
                manager.Unregister();
            }
        }
        finally
        {
            ReleaseRuntime();
        }
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs eventArgs)
    {
        var arguments = new Dictionary<string, string>(
            eventArgs.Arguments,
            StringComparer.Ordinal);
        Activated?.Invoke(
            this,
            new WindowsNotificationActivatedEventArgs(arguments));
    }

    private static WindowsNotificationAvailability Map(
        AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.Enabled =>
            WindowsNotificationAvailability.Available,
        AppNotificationSetting.DisabledForApplication =>
            WindowsNotificationAvailability.DisabledForApplication,
        AppNotificationSetting.DisabledForUser =>
            WindowsNotificationAvailability.DisabledForUser,
        AppNotificationSetting.DisabledByGroupPolicy =>
            WindowsNotificationAvailability.DisabledByGroupPolicy,
        AppNotificationSetting.DisabledByManifest =>
            WindowsNotificationAvailability.DisabledByManifest,
        _ => WindowsNotificationAvailability.Unsupported
    };

    private void ReleaseRuntime()
    {
        bool shouldShutdown;
        lock (_gate)
        {
            shouldShutdown = _runtimeInitialized;
            _runtimeInitialized = false;
        }
        if (shouldShutdown)
        {
            _bootstrap.Shutdown();
        }
    }
}
