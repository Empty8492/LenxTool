using LenxTool.App.Services;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class WindowsNotificationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledOrOsDeniedNotificationNeverCallsWindowsAdapter()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        var service = CreateService(
            adapter,
            WindowsNotificationSettings.Default);
        service.Register();
        await service.InitializeAsync(CancellationToken.None);

        await service.ProcessAsync(Notification('a'), CancellationToken.None);
        Assert.Empty(adapter.Messages);

        service.ApplySettings(EnabledSettings());
        adapter.Availability =
            WindowsNotificationAvailability.DisabledForApplication;
        await service.ProcessAsync(Notification('b'), CancellationToken.None);

        Assert.Empty(adapter.Messages);
    }

    [Fact]
    public async Task GenericPrivacyModeNeverIncludesFeedControlledLabels()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        WindowsNotificationSettings settings = EnabledSettings() with
        {
            PreviewMode = WindowsNotificationPreviewMode.GenericOnly,
            CoalesceMinutes = 0
        };
        var service = CreateService(adapter, settings);
        service.Register();
        await service.InitializeAsync(CancellationToken.None);
        AppNotification notification = Notification('a');

        await service.ProcessAsync(notification, CancellationToken.None);

        WindowsSystemNotification message = Assert.Single(adapter.Messages);
        Assert.Equal("Lenx Tools", message.Title);
        Assert.DoesNotContain(notification.Title, message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(notification.SourceLabel, message.Body, StringComparison.Ordinal);
        Assert.Equal(
            notification.Id,
            Assert.Single(message.Arguments).Value);
    }

    [Fact]
    public async Task TitleOnlyModeShowsBoundedTitleButNeverBodyOrSource()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        WindowsNotificationSettings settings = EnabledSettings() with
        {
            PreviewMode = WindowsNotificationPreviewMode.TitleOnly,
            CoalesceMinutes = 0
        };
        var service = CreateService(adapter, settings);
        service.Register();
        await service.InitializeAsync(CancellationToken.None);
        AppNotification notification = Notification('a');

        await service.ProcessAsync(notification, CancellationToken.None);

        WindowsSystemNotification message = Assert.Single(adapter.Messages);
        Assert.Equal(notification.Title, message.Title);
        Assert.Equal(notification.KindLabel, message.Body);
        Assert.DoesNotContain(notification.SourceLabel, message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuietHoursSuppressWindowsOnly()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        WindowsNotificationSettings settings = EnabledSettings() with
        {
            QuietHoursEnabled = true,
            QuietStartMinutes = 9 * 60,
            QuietEndMinutes = 11 * 60,
            CoalesceMinutes = 0
        };
        var service = CreateService(adapter, settings);
        service.Register();
        await service.InitializeAsync(CancellationToken.None);

        await service.ProcessAsync(Notification('a'), CancellationToken.None);

        Assert.Empty(adapter.Messages);
    }

    [Fact]
    public async Task FrequencyWindowCoalescesFollowingNotifications()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        var time = new MutableTimeProvider(Now);
        WindowsNotificationSettings settings = EnabledSettings() with
        {
            CoalesceMinutes = 15
        };
        var service = CreateService(adapter, settings, time);
        service.Register();
        await service.InitializeAsync(CancellationToken.None);

        await service.ProcessAsync(Notification('a'), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.ProcessAsync(Notification('b'), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.ProcessAsync(Notification('c'), CancellationToken.None);

        Assert.Single(adapter.Messages);
        time.Advance(TimeSpan.FromMinutes(13));
        await service.FlushDueAsync(CancellationToken.None);

        Assert.Equal(2, adapter.Messages.Count);
        Assert.Contains("2 条", adapter.Messages[1].Body, StringComparison.Ordinal);
        Assert.Equal(
            new string('c', 64),
            Assert.Single(adapter.Messages[1].Arguments).Value);
    }

    [Fact]
    public async Task ChangingSettingsDropsPendingAggregate()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        var time = new MutableTimeProvider(Now);
        var service = CreateService(adapter, EnabledSettings(), time);
        service.Register();
        await service.InitializeAsync(CancellationToken.None);
        await service.ProcessAsync(Notification('a'), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.ProcessAsync(Notification('b'), CancellationToken.None);

        service.ApplySettings(WindowsNotificationSettings.Default);
        time.Advance(TimeSpan.FromHours(1));
        await service.FlushDueAsync(CancellationToken.None);

        Assert.Single(adapter.Messages);
    }

    [Fact]
    public async Task DisableWinningAfterDecisionPreventsOldPreviewDelivery()
    {
        using var availabilityEntered = new ManualResetEventSlim();
        using var allowAvailability = new ManualResetEventSlim();
        var adapter = new FakeWindowsNotificationAdapter
        {
            AvailabilityEntered = availabilityEntered,
            AllowAvailability = allowAvailability
        };
        var service = CreateService(
            adapter,
            EnabledSettings() with
            {
                PreviewMode = WindowsNotificationPreviewMode.TitleOnly,
                CoalesceMinutes = 0
            });
        service.Register();
        await service.InitializeAsync(CancellationToken.None);

        Task processing = Task.Run(() => service.ProcessAsync(
            Notification('a'),
            CancellationToken.None));
        Assert.True(availabilityEntered.Wait(TimeSpan.FromSeconds(2)));
        var applyStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task applying = Task.Run(() =>
        {
            applyStarted.TrySetResult();
            service.ApplySettings(WindowsNotificationSettings.Default);
        });
        await applyStarted.Task;
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        allowAvailability.Set();

        await Task.WhenAll(processing, applying);

        Assert.Empty(adapter.Messages);
    }

    [Fact]
    public async Task EnteringQuietHoursDropsAnOlderPendingAggregate()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 8, 21, 58, 0, TimeSpan.Zero));
        WindowsNotificationSettings settings = EnabledSettings() with
        {
            QuietHoursEnabled = true,
            QuietStartMinutes = 22 * 60,
            QuietEndMinutes = 7 * 60,
            CoalesceMinutes = 15
        };
        var service = CreateService(adapter, settings, time);
        service.Register();
        await service.InitializeAsync(CancellationToken.None);
        await service.ProcessAsync(Notification('a'), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.ProcessAsync(Notification('b'), CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2));
        await service.ProcessAsync(Notification('c'), CancellationToken.None);
        time.Advance(TimeSpan.FromHours(9));
        await service.FlushDueAsync(CancellationToken.None);

        Assert.Single(adapter.Messages);
    }

    [Fact]
    public async Task TitleTruncationNeverLeavesAnUnpairedSurrogate()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        var service = CreateService(
            adapter,
            EnabledSettings() with
            {
                PreviewMode = WindowsNotificationPreviewMode.TitleOnly,
                CoalesceMinutes = 0
            });
        service.Register();
        await service.InitializeAsync(CancellationToken.None);
        AppNotification notification = Notification('a') with
        {
            Title = new string('x', 95) + "😀tail"
        };

        await service.ProcessAsync(notification, CancellationToken.None);

        string title = Assert.Single(adapter.Messages).Title;
        Assert.True(title.Length <= 96);
        Assert.False(char.IsHighSurrogate(title[^1]));
    }

    [Fact]
    public async Task ActivationIsStrictlyParsedAndDeferredUntilUiReady()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        var target = new RecordingActivationTarget();
        var service = CreateService(
            adapter,
            EnabledSettings(),
            activationTarget: target);
        service.Register();
        await service.InitializeAsync(CancellationToken.None);

        adapter.RaiseActivated(new Dictionary<string, string>
        {
            ["uri"] = "https://example.com"
        });
        adapter.RaiseActivated(
            WindowsNotificationActivation.CreateArguments(
                new string('a', 64)));

        Assert.Empty(target.OpenedIds);
        await service.SetNavigationReadyAsync(CancellationToken.None);

        Assert.Equal(new string('a', 64), Assert.Single(target.OpenedIds));
    }

    [Fact]
    public async Task EventBeforeSettingsRestoreUsesPersistedEnabledPolicy()
    {
        var adapter = new FakeWindowsNotificationAdapter();
        var inbox = new AppNotificationInbox();
        var service = CreateService(
            adapter,
            EnabledSettings() with { CoalesceMinutes = 0 },
            inbox: inbox);
        service.Register();

        inbox.Publish(Notification('a'));
        await service.InitializeAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task run = service.RunAsync(cancellation.Token);
        Task completed = await Task.WhenAny(
            adapter.MessageShown.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        await run;

        Assert.Same(adapter.MessageShown.Task, completed);
        Assert.Single(adapter.Messages);
    }

    [Fact]
    public async Task AdapterFailureDoesNotEscapeNotificationProcessing()
    {
        var adapter = new FakeWindowsNotificationAdapter
        {
            ShowFailure = new InvalidOperationException("denied")
        };
        var service = CreateService(
            adapter,
            EnabledSettings() with { CoalesceMinutes = 0 });
        service.Register();
        await service.InitializeAsync(CancellationToken.None);

        await service.ProcessAsync(Notification('a'), CancellationToken.None);

        Assert.Equal(1, adapter.ShowCalls);
    }

    private static WindowsNotificationService CreateService(
        FakeWindowsNotificationAdapter adapter,
        WindowsNotificationSettings settings,
        MutableTimeProvider? time = null,
        RecordingActivationTarget? activationTarget = null,
        AppNotificationInbox? inbox = null) =>
        new(
            adapter,
            new StubSettingsStore(settings),
            inbox ?? new AppNotificationInbox(),
            activationTarget ?? new RecordingActivationTarget(),
            time ?? new MutableTimeProvider(Now));

    private static WindowsNotificationSettings EnabledSettings() =>
        WindowsNotificationSettings.Default with
        {
            Enabled = true,
            QuietHoursEnabled = false
        };

    private static AppNotification Notification(char key) =>
        new(
            new string(key, 64),
            "entry-" + key,
            "feed-1",
            Guid.Empty.ToString("D"),
            1,
            "用户可控标题 " + key,
            "用户可控来源",
            Now,
            ReadAt: null,
            AppNotificationKind.ContentMatch,
            AppNotificationTargetKind.FeedEntry,
            "entry-" + key);

    private sealed class StubSettingsStore(
        WindowsNotificationSettings settings)
        : IWindowsNotificationSettingsStore
    {
        public Task<WindowsNotificationSettings> GetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(settings);

        public Task SaveAsync(
            WindowsNotificationSettings value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWindowsNotificationAdapter
        : IWindowsNotificationAdapter
    {
        private WindowsNotificationAvailability _availability =
            WindowsNotificationAvailability.Available;

        public event EventHandler<WindowsNotificationActivatedEventArgs>?
            Activated;

        public WindowsNotificationAvailability Availability
        {
            get
            {
                AvailabilityEntered?.Set();
                if (AllowAvailability is not null &&
                    !AllowAvailability.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Availability test barrier timed out.");
                }
                return _availability;
            }
            set => _availability = value;
        }
        public ManualResetEventSlim? AvailabilityEntered { get; init; }
        public ManualResetEventSlim? AllowAvailability { get; init; }
        public List<WindowsSystemNotification> Messages { get; } = [];
        public Exception? ShowFailure { get; init; }
        public int ShowCalls { get; private set; }
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }
        public TaskCompletionSource MessageShown { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Register() => RegisterCalls++;

        public void Show(WindowsSystemNotification notification)
        {
            ShowCalls++;
            if (ShowFailure is not null)
            {
                throw ShowFailure;
            }
            Messages.Add(notification);
            MessageShown.TrySetResult();
        }

        public void Unregister() => UnregisterCalls++;

        public void RaiseActivated(
            IReadOnlyDictionary<string, string> arguments) =>
            Activated?.Invoke(
                this,
                new WindowsNotificationActivatedEventArgs(arguments));
    }

    private sealed class RecordingActivationTarget
        : IWindowsNotificationActivationTarget
    {
        public List<string> OpenedIds { get; } = [];

        public Task OpenAsync(
            string notificationId,
            CancellationToken cancellationToken)
        {
            OpenedIds.Add(notificationId);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }
}
