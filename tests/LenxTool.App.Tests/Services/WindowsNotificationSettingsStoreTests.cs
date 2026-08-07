using LenxTool.App.Services;
using LenxTool.Core.Contracts;

namespace LenxTool.App.Tests.Services;

public sealed class WindowsNotificationSettingsStoreTests
{
    [Fact]
    public async Task MissingValueReturnsPrivacyPreservingDisabledDefaults()
    {
        var settings = new FakeSettingsRepository();
        var store = new AppSettingsWindowsNotificationSettingsStore(settings);

        WindowsNotificationSettings value =
            await store.GetAsync(CancellationToken.None);

        Assert.False(value.Enabled);
        Assert.Equal(
            WindowsNotificationPreviewMode.GenericOnly,
            value.PreviewMode);
        Assert.True(value.QuietHoursEnabled);
        Assert.Equal(22 * 60, value.QuietStartMinutes);
        Assert.Equal(7 * 60, value.QuietEndMinutes);
        Assert.Equal(15, value.CoalesceMinutes);
    }

    [Fact]
    public async Task RoundTripPersistsOnlyValidatedNonSecretSettings()
    {
        var settings = new FakeSettingsRepository();
        var store = new AppSettingsWindowsNotificationSettingsStore(settings);
        var expected = new WindowsNotificationSettings(
            Enabled: true,
            WindowsNotificationPreviewMode.TitleOnly,
            QuietHoursEnabled: true,
            QuietStartMinutes: 23 * 60 + 30,
            QuietEndMinutes: 6 * 60 + 15,
            CoalesceMinutes: 30);

        await store.SaveAsync(expected, CancellationToken.None);
        WindowsNotificationSettings actual =
            await store.GetAsync(CancellationToken.None);

        Assert.Equal(expected, actual);
        string persisted = Assert.Single(settings.Values).Value;
        Assert.DoesNotContain("title", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uri", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":1,\"enabled\":true,\"previewMode\":99}")]
    [InlineData("{\"schemaVersion\":2,\"enabled\":true}")]
    public async Task CorruptOrUnknownValueFallsBackToDisabledDefaults(
        string persisted)
    {
        var settings = new FakeSettingsRepository();
        settings.Values[
            AppSettingsWindowsNotificationSettingsStore.SettingsKey] =
            persisted;
        var store = new AppSettingsWindowsNotificationSettingsStore(settings);

        WindowsNotificationSettings value =
            await store.GetAsync(CancellationToken.None);

        Assert.Equal(WindowsNotificationSettings.Default, value);
    }

    private sealed class FakeSettingsRepository : IAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<string?> GetAsync(
            string key,
            CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }
}
