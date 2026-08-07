using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class WindowsNotificationSettingsLayoutTests
{
    [Fact]
    public void SettingsExposeExplicitOptInPrivacyQuietHoursAndCoalescing()
    {
        XDocument document = XDocument.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "App.xaml"));
        string text = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains(
            "DataContext=\"{Binding WindowsNotifications}\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"启用 Windows 系统通知\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"Windows 通知隐私说明\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding PreviewModes}\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding CoalesceOptions}\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding QuietStartText, UpdateSourceTrigger=PropertyChanged}\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding QuietEndText, UpdateSourceTrigger=PropertyChanged}\"",
            text,
            StringComparison.Ordinal);
    }
}
