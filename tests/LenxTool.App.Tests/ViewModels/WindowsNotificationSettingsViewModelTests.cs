using LenxTool.App.Services;
using LenxTool.App.ViewModels;

namespace LenxTool.App.Tests.ViewModels;

public sealed class WindowsNotificationSettingsViewModelTests
{
    [Fact]
    public async Task InitializeRestoresPersistedSettingsAndHonestAvailability()
    {
        WindowsNotificationSettings settings =
            WindowsNotificationSettings.Default with
            {
                Enabled = true,
                PreviewMode = WindowsNotificationPreviewMode.TitleOnly,
                QuietStartMinutes = 21 * 60 + 30,
                QuietEndMinutes = 6 * 60 + 15,
                CoalesceMinutes = 30
            };
        var controller = new FakeController
        {
            Availability =
                WindowsNotificationAvailability.DisabledForUser
        };
        var viewModel = new WindowsNotificationSettingsViewModel(
            new FakeStore(settings),
            controller);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.Enabled);
        Assert.Equal(
            WindowsNotificationPreviewMode.TitleOnly,
            viewModel.PreviewMode);
        Assert.Equal("21:30", viewModel.QuietStartText);
        Assert.Equal("06:15", viewModel.QuietEndText);
        Assert.Equal(30, viewModel.CoalesceMinutes);
        Assert.Contains("Windows", viewModel.AvailabilityStatus);
        Assert.Contains("关闭", viewModel.AvailabilityStatus);
        Assert.Equal(settings, controller.Settings);
        Assert.Equal(1, controller.ApplyCount);
    }

    [Fact]
    public async Task InitializeDoesNotResetAnAlreadyActivePolicy()
    {
        WindowsNotificationSettings settings =
            WindowsNotificationSettings.Default with { Enabled = true };
        var controller = new FakeController(settings);
        var viewModel = new WindowsNotificationSettingsViewModel(
            new FakeStore(settings),
            controller);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(settings, controller.Settings);
        Assert.Equal(0, controller.ApplyCount);
    }

    [Fact]
    public async Task SavePersistsOneValidatedPolicyAndAppliesItImmediately()
    {
        var store = new FakeStore(WindowsNotificationSettings.Default);
        var controller = new FakeController();
        var viewModel = new WindowsNotificationSettingsViewModel(
            store,
            controller)
        {
            Enabled = true,
            PreviewMode = WindowsNotificationPreviewMode.TitleOnly,
            QuietHoursEnabled = true,
            QuietStartText = "23:15",
            QuietEndText = "07:45",
            CoalesceMinutes = 5
        };

        await viewModel.SaveCommand.ExecuteAsync();

        WindowsNotificationSettings saved = Assert.Single(store.Saved);
        Assert.Equal(23 * 60 + 15, saved.QuietStartMinutes);
        Assert.Equal(7 * 60 + 45, saved.QuietEndMinutes);
        Assert.Equal(saved, controller.Settings);
        Assert.Contains("已保存", viewModel.Status);
    }

    [Theory]
    [InlineData("7:00", "08:00")]
    [InlineData("07:00", "07:00")]
    [InlineData("24:00", "08:00")]
    public async Task InvalidQuietHoursFailClosedWithoutPersistence(
        string start,
        string end)
    {
        var store = new FakeStore(WindowsNotificationSettings.Default);
        var controller = new FakeController();
        var viewModel = new WindowsNotificationSettingsViewModel(
            store,
            controller)
        {
            QuietHoursEnabled = true,
            QuietStartText = start,
            QuietEndText = end
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Empty(store.Saved);
        Assert.Contains("HH:mm", viewModel.Status);
    }

    private sealed class FakeStore(WindowsNotificationSettings initial)
        : IWindowsNotificationSettingsStore
    {
        public List<WindowsNotificationSettings> Saved { get; } = [];

        public Task<WindowsNotificationSettings> GetAsync(
            CancellationToken cancellationToken) => Task.FromResult(initial);

        public Task SaveAsync(
            WindowsNotificationSettings settings,
            CancellationToken cancellationToken)
        {
            Saved.Add(settings);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeController : IWindowsNotificationController
    {
        public FakeController(
            WindowsNotificationSettings? settings = null)
        {
            Settings = settings ?? WindowsNotificationSettings.Default;
        }

        public WindowsNotificationAvailability Availability { get; init; } =
            WindowsNotificationAvailability.Available;

        public WindowsNotificationSettings Settings { get; private set; }

        public int ApplyCount { get; private set; }

        public void ApplySettings(WindowsNotificationSettings settings)
        {
            ApplyCount++;
            Settings = settings;
        }
    }
}
