using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class DashboardLayoutTests
{
    [Fact]
    public void DashboardBindsLiveStatusesAndFavoriteCount()
    {
        XElement template = LoadDashboardTemplate();

        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding TrendStatus}");
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding FavoriteSummary}");
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding DataStatus}");
        Assert.DoesNotContain(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value.Contains("08:32", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DashboardShowsStructuredDailyOverviewOnWarmTrendSurface()
    {
        XElement template = LoadDashboardTemplate();

        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding BriefingSections}");
        Assert.DoesNotContain(
            template.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding News}");

        XElement trendTitle = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "热点脉搏");
        XElement trendCard = trendTitle.Ancestors()
            .First(element => element.Name.LocalName == "Border"
                && element.Attribute("Style")?.Value.Contains(
                    "BentoCardStyle",
                    StringComparison.Ordinal) == true);

        Assert.Equal(
            "{DynamicResource Brush.SurfaceMuted}",
            trendCard.Attribute("Background")?.Value);
    }

    [Fact]
    public void DashboardStartsWithOverviewAndLinksDirectlyToDailyBriefing()
    {
        XElement template = LoadDashboardTemplate();

        Assert.DoesNotContain(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value is
                    "{Binding NewsStatus}" or
                    "{Binding BriefingMeta}" or
                    "{Binding BriefingTitle}");

        XElement overview = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding BriefingSections}");
        Assert.Equal("0,0,0,2", overview.Attribute("Margin")?.Value);

        XElement openDailyBriefing = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Content")?.Value == "查看完整早报");
        Assert.Equal(
            "daily-briefing",
            openDailyBriefing.Attribute("CommandParameter")?.Value);
    }

    [Fact]
    public void ShellUsesOnePageTitleAndTwoContinuousSurfaces()
    {
        XDocument document = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding CurrentPage.Title}");

        XElement sidebar = Assert.Single(
            document.Descendants(),
            element => element.Attribute(x + "Name")?.Value == "SidebarSurface");
        Assert.Equal("0,0,1,0", sidebar.Attribute("BorderThickness")?.Value);

        Assert.Contains(
            document.Descendants(),
            element => element.Name.LocalName == "Grid"
                && element.Attribute("Grid.Column")?.Value == "1"
                && element.Attribute("Background")?.Value
                    == "{DynamicResource Brush.Window}");
    }

    [Fact]
    public void ShellTitleBarContinuesBothSurfacesWithoutHorizontalDividers()
    {
        XDocument document = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml"));
        XElement window = Assert.IsType<XElement>(document.Root);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Equal("None", window.Attribute("WindowStyle")?.Value);
        XElement chrome = Assert.Single(
            window.Descendants(),
            element => element.Name.LocalName == "WindowChrome");
        Assert.Equal("36", chrome.Attribute("CaptionHeight")?.Value);
        Assert.Equal("0", chrome.Attribute("GlassFrameThickness")?.Value);
        Assert.Equal("False", chrome.Attribute("UseAeroCaptionButtons")?.Value);

        XElement titleBar = Assert.Single(
            window.Descendants(),
            element => element.Attribute(x + "Name")?.Value == "AppTitleBar");
        XElement leftSurface = Assert.Single(
            titleBar.Descendants(),
            element => element.Attribute(x + "Name")?.Value == "TitleBarSidebarSurface");
        XElement rightSurface = Assert.Single(
            titleBar.Descendants(),
            element => element.Attribute(x + "Name")?.Value == "TitleBarWorkspaceSurface");
        Assert.Equal(
            "{DynamicResource Brush.Sidebar}",
            leftSurface.Attribute("Background")?.Value);
        Assert.Equal(
            "{DynamicResource Brush.Window}",
            rightSurface.Attribute("Background")?.Value);
        Assert.Null(leftSurface.Attribute("BorderThickness"));
        Assert.Null(rightSurface.Attribute("BorderThickness"));

        XElement workspaceHeader = Assert.Single(
            window.Descendants(),
            element => element.Attribute(x + "Name")?.Value == "WorkspaceHeader");
        Assert.Null(workspaceHeader.Attribute("BorderThickness"));

        string[] windowActions = ["最小化窗口", "最大化或还原窗口", "关闭窗口"];
        Assert.All(
            windowActions,
            action => Assert.Contains(
                titleBar.Descendants(),
                element => element.Name.LocalName == "Button"
                    && element.Attribute("AutomationProperties.Name")?.Value == action
                    && element.Attributes().Any(attribute =>
                        attribute.Name.LocalName == "WindowChrome.IsHitTestVisibleInChrome"
                        && attribute.Value == "True")
                    && !string.IsNullOrWhiteSpace(element.Attribute("Click")?.Value)));
    }

    private static XElement LoadDashboardTemplate()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml");
        XDocument document = XDocument.Load(xamlPath);
        return Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value.Contains(
                    "DashboardViewModel",
                    StringComparison.Ordinal) == true);
    }
}
