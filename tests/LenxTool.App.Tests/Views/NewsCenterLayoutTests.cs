using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class NewsCenterLayoutTests
{
    [Fact]
    public void NewsReaderUsesCompactHeaderAndNonStretchingRefreshAction()
    {
        XElement template = LoadNewsCenterTemplate();

        XElement refreshButton = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Content")?.Value == "刷新今日资讯");
        Assert.Equal("Center", refreshButton.Attribute("VerticalAlignment")?.Value);

        XElement dailyTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "每日早报");
        XElement articleView = Assert.Single(
            dailyTab.Descendants(),
            element => element.Name.LocalName == "RichArticleView");
        XElement articleGrid = Assert.IsType<XElement>(articleView.Parent);
        XElement[] articleRows = articleGrid
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToArray();
        Assert.Equal("56", articleRows[0].Attribute("Height")?.Value);
    }

    [Fact]
    public void AiReportReaderReservesMoreHeightForContent()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement reportBody = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attributes().Any(attribute => attribute.Value == "AI 报告正文"));
        XElement reportGrid = Assert.IsType<XElement>(reportBody.Parent);
        XElement[] rows = reportGrid
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToArray();

        Assert.Equal("56", rows[0].Attribute("Height")?.Value);
        Assert.Equal("40", rows[2].Attribute("Height")?.Value);
    }

    [Fact]
    public void NewsPageUsesAnimatedOuterScrollerAndDeferredBackToTopAction()
    {
        XElement template = LoadNewsCenterTemplate();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement pageScroller = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "AnimatedScrollViewer"
                && element.Attribute(x + "Name")?.Value == "NewsPageScrollViewer");
        Assert.Equal("Visible", pageScroller.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("Disabled", pageScroller.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Contains(pageScroller.Descendants(), element => element.Name.LocalName == "TabControl");
        Assert.Contains(
            pageScroller.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding Title}");

        XElement backToTopButton = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "回到页面顶部");
        Assert.Contains("SmoothScrollToTopCommand", backToTopButton.Attribute("Command")?.Value);
        Assert.Contains("NewsPageScrollViewer", backToTopButton.Attribute("CommandTarget")?.Value);
        Assert.Contains(
            backToTopButton.Descendants(),
            element => element.Name.LocalName == "DataTrigger"
                && element.Attribute("Binding")?.Value.Contains("IsBackToTopVisible", StringComparison.Ordinal) == true);
        Assert.Contains(
            backToTopButton.Descendants(),
            element => element.Name.LocalName == "DoubleAnimation"
                && element.Attributes().Any(attribute => attribute.Value == "Opacity")
                && element.Attribute("To")?.Value == "1");
    }

    [Fact]
    public void TrendPageUsesGroupedClickableItemsWithoutNestedListScroller()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement trendTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "热点趋势");

        Assert.DoesNotContain(trendTab.Descendants(), element => element.Name.LocalName == "ListBox");
        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding TrendGroups}");
        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value.Contains("OpenTrendCommand", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void TrendPageProvidesVisibleMultiSourceFilters()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement trendTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "热点趋势");

        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding SourceFilters}");
        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "ToggleButton"
                && element.Attribute("Style")?.Value.Contains("FilterChipStyle", StringComparison.Ordinal) == true
                && element.Attribute("IsChecked")?.Value.Contains("IsSelected", StringComparison.Ordinal) == true);
        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value == "{Binding SelectAllSourcesCommand}");
    }

    [Fact]
    public void SelectedNewsTabUsesAnIntentionalBottomIndicator()
    {
        XElement template = LoadNewsCenterTemplate();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement indicator = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "Border"
                && element.Attribute(x + "Name")?.Value == "SelectedIndicator");
        Assert.Equal("2", indicator.Attribute("Height")?.Value);
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "Trigger"
                && element.Attribute("Property")?.Value == "IsSelected"
                && element.Descendants().Any(setter =>
                    setter.Name.LocalName == "Setter"
                    && setter.Attribute("TargetName")?.Value == "SelectedIndicator"));
    }

    [Fact]
    public void FeedTimelineUsesRecyclingVirtualizationAndScrollPaging()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement timelineTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "Feed 时间线");
        Assert.Contains(
            timelineTab.Descendants(),
            element => element.Name.LocalName == "FeedTimelineView");
        XElement timelineBrowser = LoadFixture("FeedTimelineBrowserView.xaml");
        XElement timeline = Assert.Single(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "PagedListBox");

        Assert.Equal("{Binding TimelineEntries}", timeline.Attribute("ItemsSource")?.Value);
        Assert.Equal(
            "{Binding LoadMoreTimelineCommand}",
            timeline.Attribute("LoadMoreCommand")?.Value);
        Assert.Equal("True", timeline.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.Equal("Recycling", timeline.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);
        Assert.Equal("True", timeline.Attribute("ScrollViewer.CanContentScroll")?.Value);
    }

    [Fact]
    public void FeedTimelineProvidesReadOnlyFiltersAndNativeReader()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement timelineTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "Feed 时间线");
        Assert.Contains(
            timelineTab.Descendants(),
            element => element.Name.LocalName == "FeedTimelineView");
        XElement timelineFilters = LoadFixture("FeedTimelineFiltersView.xaml");
        XElement timelineBrowser = LoadFixture("FeedTimelineBrowserView.xaml");
        string[] automationNames =
        [
            "Feed 分类筛选",
            "Feed 来源筛选",
            "Feed 日期筛选",
            "Feed 关键词筛选"
        ];

        Assert.All(
            automationNames,
            name => Assert.Contains(
                timelineFilters.Descendants(),
                element => element.Attribute("AutomationProperties.Name")?.Value == name));
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "RichArticleView"
                && element.Attribute("Article")?.Value == "{Binding SelectedFeedArticle}");
        Assert.DoesNotContain(
            timelineFilters.Descendants().Concat(timelineBrowser.Descendants()),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Content")?.Value is "新增" or "编辑" or "删除" or "订阅管理");
    }

    [Fact]
    public void FeedReaderProvidesKeyboardAccessiblePrivateNoteAndTagEditor()
    {
        XElement timelineBrowser = LoadFixture("FeedTimelineBrowserView.xaml");

        XElement note = Assert.Single(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("AutomationProperties.Name")?.Value == "Feed 私人备注");
        Assert.Contains("SelectedTimelineNote", note.Attribute("Text")?.Value);
        Assert.Equal("4000", note.Attribute("MaxLength")?.Value);
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "保存 Feed 私人备注"
                && element.Attribute("Command")?.Value == "{Binding SaveTimelineNoteCommand}");

        XElement tagInput = Assert.Single(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("AutomationProperties.Name")?.Value == "新增 Feed 标签");
        Assert.Contains("TimelineTagInput", tagInput.Attribute("Text")?.Value);
        Assert.Equal("80", tagInput.Attribute("MaxLength")?.Value);
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding SelectedTimelineTags}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value.Contains(
                    "RemoveTimelineTagCommand",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding TimelineEditorStatus}");
    }

    private static XElement LoadNewsCenterTemplate()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml");
        XDocument document = XDocument.Load(xamlPath);
        return Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value.Contains(
                    "NewsCenterViewModel",
                    StringComparison.Ordinal) == true);
    }

    private static XElement LoadFixture(string fileName)
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return XDocument.Load(xamlPath).Root
            ?? throw new InvalidDataException($"{fileName} 没有根元素。");
    }
}
