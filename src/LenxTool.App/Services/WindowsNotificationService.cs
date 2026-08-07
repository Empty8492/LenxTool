using System.Threading.Channels;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public enum WindowsNotificationAvailability
{
    Available,
    DisabledForApplication,
    DisabledForUser,
    DisabledByGroupPolicy,
    DisabledByManifest,
    Unsupported,
    RegistrationFailed
}

public sealed record WindowsSystemNotification(
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Arguments);

public sealed class WindowsNotificationActivatedEventArgs(
    IReadOnlyDictionary<string, string> arguments) : EventArgs
{
    public IReadOnlyDictionary<string, string> Arguments { get; } =
        arguments ?? throw new ArgumentNullException(nameof(arguments));
}

public interface IWindowsNotificationAdapter
{
    event EventHandler<WindowsNotificationActivatedEventArgs>? Activated;

    WindowsNotificationAvailability Availability { get; }

    void Register();

    void Show(WindowsSystemNotification notification);

    void Unregister();
}

public interface IWindowsNotificationActivationTarget
{
    Task OpenAsync(
        string notificationId,
        CancellationToken cancellationToken);
}

public interface IWindowsNotificationController
{
    WindowsNotificationAvailability Availability { get; }

    WindowsNotificationSettings Settings { get; }

    void ApplySettings(WindowsNotificationSettings settings);
}

public sealed class WindowsNotificationService : IWindowsNotificationController
{
    private const int MaxPendingActivations = 16;
    private const int MaxSystemTitleLength = 96;
    private readonly IWindowsNotificationAdapter _adapter;
    private readonly IWindowsNotificationSettingsStore _settingsStore;
    private readonly IWindowsNotificationActivationTarget _activationTarget;
    private readonly TimeProvider _timeProvider;
    private readonly WindowsNotificationCoalescer _coalescer = new();
    private readonly Channel<AppNotification> _inboxChannel;
    private readonly object _gate = new();
    private readonly object _deliveryGate = new();
    private readonly Queue<string> _pendingActivations = new();
    private readonly TaskCompletionSource _settingsReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private WindowsNotificationSettings _settings =
        WindowsNotificationSettings.Default;
    private bool _registered;
    private bool _navigationReady;

    public WindowsNotificationService(
        IWindowsNotificationAdapter adapter,
        IWindowsNotificationSettingsStore settingsStore,
        IAppNotificationInbox inbox,
        IWindowsNotificationActivationTarget activationTarget,
        TimeProvider timeProvider)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _settingsStore = settingsStore ??
            throw new ArgumentNullException(nameof(settingsStore));
        _activationTarget = activationTarget ??
            throw new ArgumentNullException(nameof(activationTarget));
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(inbox);

        _inboxChannel = Channel.CreateBounded<AppNotification>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });
        inbox.NotificationReceived += OnNotificationReceived;
    }

    public WindowsNotificationAvailability Availability
    {
        get
        {
            try
            {
                return _adapter.Availability;
            }
            catch
            {
                return WindowsNotificationAvailability.Unsupported;
            }
        }
    }

    public WindowsNotificationSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public void Register()
    {
        lock (_gate)
        {
            if (_registered)
            {
                return;
            }
            _registered = true;
        }

        _adapter.Activated += OnActivated;
        try
        {
            _adapter.Register();
        }
        catch
        {
            // Windows notification availability is optional. The durable
            // in-app inbox remains the source of truth when registration fails.
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        WindowsNotificationSettings settings =
            await _settingsStore.GetAsync(cancellationToken)
                .ConfigureAwait(false);
        ApplySettings(settings);
        _settingsReady.TrySetResult();
    }

    public void ApplySettings(WindowsNotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_deliveryGate)
        {
            lock (_gate)
            {
                _settings = settings;
                _coalescer.Reset();
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(
            TimeSpan.FromSeconds(1),
            _timeProvider);
        try
        {
            await _settingsReady.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                while (_inboxChannel.Reader.TryRead(
                           out AppNotification? notification))
                {
                    await ProcessAsync(notification, cancellationToken)
                        .ConfigureAwait(false);
                }

                await FlushDueAsync(cancellationToken)
                    .ConfigureAwait(false);
                await timer.WaitForNextTickAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal Task ProcessAsync(
        AppNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();

        WindowsNotificationSettings settings;
        WindowsNotificationCoalescingDecision decision;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            settings = _settings;
            if (!CanShow(settings, now))
            {
                _coalescer.Reset();
                return Task.CompletedTask;
            }

            decision = _coalescer.Add(
                notification,
                now,
                TimeSpan.FromMinutes(settings.CoalesceMinutes));
        }

        if (decision.Outcome ==
            WindowsNotificationCoalescingOutcome.ShowImmediately)
        {
            TryShowSingle(notification);
        }
        return Task.CompletedTask;
    }

    internal Task FlushDueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsNotificationBatch? batch;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!CanShow(_settings, now))
            {
                _coalescer.Reset();
                return Task.CompletedTask;
            }
            batch = _coalescer.TakeDue(now);
        }

        if (batch is not null)
        {
            TryShowBatch(batch);
        }
        return Task.CompletedTask;
    }

    public async Task SetNavigationReadyAsync(
        CancellationToken cancellationToken)
    {
        List<string> pending;
        lock (_gate)
        {
            _navigationReady = true;
            pending = [.. _pendingActivations];
            _pendingActivations.Clear();
        }

        foreach (string notificationId in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await OpenSafelyAsync(notificationId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void Unregister()
    {
        bool shouldUnregister;
        lock (_gate)
        {
            shouldUnregister = _registered;
            _registered = false;
            _navigationReady = false;
            _pendingActivations.Clear();
            _coalescer.Reset();
        }
        if (!shouldUnregister)
        {
            return;
        }

        lock (_deliveryGate)
        {
            _adapter.Activated -= OnActivated;
            try
            {
                _adapter.Unregister();
            }
            catch
            {
            }
        }
    }

    private bool CanShow(
        WindowsNotificationSettings settings,
        DateTimeOffset now)
    {
        if (!settings.Enabled || Availability !=
            WindowsNotificationAvailability.Available)
        {
            return false;
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(
            now,
            _timeProvider.LocalTimeZone);
        return !WindowsNotificationPolicy.IsQuietTime(
            settings,
            TimeOnly.FromDateTime(local.DateTime));
    }

    private static WindowsSystemNotification CreateSingleMessage(
        AppNotification notification,
        WindowsNotificationSettings settings)
    {
        string title;
        string body;
        if (settings.PreviewMode ==
            WindowsNotificationPreviewMode.TitleOnly)
        {
            title = TruncateTitle(notification.Title);
            body = notification.KindLabel;
        }
        else
        {
            title = "Lenx Tools";
            body = "有一条新通知";
        }

        return new(
            title,
            body,
            WindowsNotificationActivation.CreateArguments(notification.Id));
    }

    private static string TruncateTitle(string value)
    {
        if (value.Length <= MaxSystemTitleLength)
        {
            return value;
        }

        int length = MaxSystemTitleLength;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }
        return value[..length];
    }

    private void TryShowSingle(AppNotification notification)
    {
        lock (_deliveryGate)
        {
            WindowsNotificationSettings? settings =
                GetCurrentDeliverySettings();
            if (settings is null)
            {
                return;
            }
            TryShowCore(CreateSingleMessage(notification, settings));
        }
    }

    private void TryShowBatch(WindowsNotificationBatch batch)
    {
        lock (_deliveryGate)
        {
            if (GetCurrentDeliverySettings() is null)
            {
                return;
            }
            TryShowCore(new WindowsSystemNotification(
                "Lenx Tools",
                $"还有 {batch.Count} 条新通知",
                WindowsNotificationActivation.CreateArguments(
                    batch.Latest.Id)));
        }
    }

    private WindowsNotificationSettings? GetCurrentDeliverySettings()
    {
        WindowsNotificationSettings settings;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            settings = _settings;
            if (!CanShow(settings, now))
            {
                _coalescer.Reset();
                return null;
            }
        }
        return settings;
    }

    private void TryShowCore(WindowsSystemNotification notification)
    {
        try
        {
            _adapter.Show(notification);
        }
        catch
        {
            // A platform failure must not roll back or poison the durable inbox.
        }
    }

    private void OnNotificationReceived(AppNotification notification)
    {
        WindowsNotificationSettings settings;
        lock (_gate)
        {
            settings = _settings;
        }
        if (!_settingsReady.Task.IsCompleted || settings.Enabled)
        {
            _inboxChannel.Writer.TryWrite(notification);
        }
    }

    private void OnActivated(
        object? sender,
        WindowsNotificationActivatedEventArgs eventArgs)
    {
        if (!WindowsNotificationActivation.TryParse(
                eventArgs.Arguments,
                out string? notificationId))
        {
            return;
        }
        string validNotificationId = notificationId!;

        lock (_gate)
        {
            if (!_navigationReady)
            {
                if (_pendingActivations.Count < MaxPendingActivations &&
                    !_pendingActivations.Contains(validNotificationId))
                {
                    _pendingActivations.Enqueue(validNotificationId);
                }
                return;
            }
        }

        _ = OpenSafelyAsync(validNotificationId, CancellationToken.None);
    }

    private async Task OpenSafelyAsync(
        string notificationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _activationTarget.OpenAsync(
                notificationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }
}
