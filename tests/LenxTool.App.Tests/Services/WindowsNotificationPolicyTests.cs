using LenxTool.App.Services;

namespace LenxTool.App.Tests.Services;

public sealed class WindowsNotificationPolicyTests
{
    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(0, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    public void OvernightQuietHoursUseStartInclusiveEndExclusive(
        int hour,
        int minute,
        bool expected)
    {
        WindowsNotificationSettings settings =
            WindowsNotificationSettings.Default with
            {
                Enabled = true,
                QuietHoursEnabled = true,
                QuietStartMinutes = 22 * 60,
                QuietEndMinutes = 7 * 60
            };

        bool actual = WindowsNotificationPolicy.IsQuietTime(
            settings,
            new TimeOnly(hour, minute));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SameDayQuietHoursAreSupported()
    {
        WindowsNotificationSettings settings =
            WindowsNotificationSettings.Default with
            {
                QuietStartMinutes = 12 * 60,
                QuietEndMinutes = 13 * 60
            };

        Assert.False(WindowsNotificationPolicy.IsQuietTime(
            settings,
            new TimeOnly(11, 59)));
        Assert.True(WindowsNotificationPolicy.IsQuietTime(
            settings,
            new TimeOnly(12, 0)));
        Assert.False(WindowsNotificationPolicy.IsQuietTime(
            settings,
            new TimeOnly(13, 0)));
    }

    [Fact]
    public void InvalidSettingsAreRejectedBeforePersistence()
    {
        WindowsNotificationSettings invalid =
            WindowsNotificationSettings.Default with
            {
                QuietStartMinutes = 24 * 60,
                CoalesceMinutes = 7
            };

        Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate);
    }
}
