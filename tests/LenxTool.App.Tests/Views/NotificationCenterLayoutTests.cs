using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class NotificationCenterLayoutTests
{
    [Fact]
    public void NotificationPopupExposesTypedFilterAndBoundedContentOnly()
    {
        XDocument document = XDocument.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "MainWindow.xaml"));
        string text = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains(
            "AutomationProperties.Name=\"通知类别筛选\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding KindFilters}\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedValue=\"{Binding SelectedKindFilter, Mode=TwoWay}\"",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding KindLabel}\"",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SanitizedContent",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Summary}",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NormalizedUrl",
            text,
            StringComparison.Ordinal);
    }
}
