using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class FeedAdminLayoutTests
{
    [Fact]
    public void AdminTemplateExposesAccessibleCatalogListsEditorsAndSafeDiscoveryPreview()
    {
        XDocument document = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        XElement template = document.Descendants().Single(element =>
            element.Name.LocalName == "DataTemplate"
            && element.Attribute("DataType")?.Value.Contains("FeedAdminViewModel", StringComparison.Ordinal) == true);

        foreach (string automationName in new[]
        {
            "共享分类列表",
            "共享 Feed 列表",
            "分类名称",
            "分类排序",
            "分类手动摘要策略",
            "分类自动摘要策略",
            "分类自动翻译策略",
            "分类翻译目标语言",
            "分类每日 AI 条目上限",
            "分类 AI 并发上限",
            "分类 AI 用量预估",
            "Feed 地址",
            "Feed 显示名称",
            "Feed 站点地址",
            "Feed 分类",
            "Feed 视图类型",
            "Feed 刷新间隔",
            "Feed 排序",
            "Feed 手动摘要策略",
            "Feed 自动摘要策略",
            "Feed 自动翻译策略",
            "Feed 翻译目标语言",
            "Feed 每日 AI 条目上限",
            "Feed AI 并发上限",
            "Feed AI 用量预估",
            "Feed 安全验证预览",
            "OPML 预览摘要",
            "OPML 导入预览列表",
            "选择此 OPML 项"
        })
        {
            Assert.Contains(template.Descendants(), element =>
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "AutomationProperties.Name"
                    && attribute.Value == automationName));
        }

        foreach (string command in new[]
        {
            "RefreshCommand",
            "BeginNewCategoryCommand",
            "SaveCategoryCommand",
            "ToggleCategoryCommand",
            "MoveCategoryUpCommand",
            "MoveCategoryDownCommand",
            "PrepareDeleteCategoryCommand",
            "ConfirmDeleteCategoryCommand",
            "BeginNewFeedCommand",
            "DiscoverCommand",
            "SaveFeedCommand",
            "ToggleFeedCommand",
            "MoveFeedUpCommand",
            "MoveFeedDownCommand",
            "PrepareDeleteFeedCommand",
            "ConfirmDeleteFeedCommand",
            "PreviewOpmlCommand",
            "ImportSelectedOpmlCommand",
            "SelectAllNewOpmlCommand",
            "ClearOpmlSelectionCommand",
            "ExportOpmlCommand"
        })
        {
            Assert.Contains(template.Descendants(), element =>
                element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value.Contains(command, StringComparison.Ordinal) == true);
        }

        XElement preview = template.Descendants().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name"
                && attribute.Value == "Feed 安全验证预览"));
        foreach (string binding in new[] { "DiscoveryTitle", "DiscoverySite", "DiscoveryType", "DiscoveryWarning" })
        {
            Assert.Contains(preview.Descendants(), element =>
                element.Attribute("Text")?.Value.Contains(binding, StringComparison.Ordinal) == true);
        }

        foreach (string binding in new[] { "OpmlItems", "IsSelected", "StatusLabel", "Message" })
        {
            Assert.Contains(template.DescendantsAndSelf(), element =>
                element.Attributes().Any(attribute =>
                    attribute.Value.Contains(binding, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void AdminTemplateKeepsNarrowWindowUsableAndReportsLiveStatus()
    {
        XDocument document = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        XElement template = document.Descendants().Single(element =>
            element.Name.LocalName == "DataTemplate"
            && element.Attribute("DataType")?.Value.Contains("FeedAdminViewModel", StringComparison.Ordinal) == true);

        XElement scroller = Assert.Single(
            template.Elements(),
            element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal("Auto", scroller.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("Auto", scroller.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Contains(template.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value.Contains("Status", StringComparison.Ordinal) == true
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.LiveSetting"
                && attribute.Value == "Polite"));
    }

    [Fact]
    public void HealthTabExposesVirtualizedDiagnosticsAndSafeRetry()
    {
        XDocument app = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        XElement template = app.Descendants().Single(element =>
            element.Name.LocalName == "DataTemplate"
            && element.Attribute("DataType")?.Value.Contains("FeedAdminViewModel", StringComparison.Ordinal) == true);
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "健康"
                && element.Descendants().Any(child => child.Name.LocalName == "FeedHealthView"));

        XDocument health = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "FeedHealthView.xaml"));
        XElement list = Assert.Single(
            health.Descendants(),
            element => element.Name.LocalName == "ListBox");
        Assert.Equal("{Binding HealthItems}", list.Attribute("ItemsSource")?.Value);
        Assert.Equal("True", list.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.Equal("Recycling", list.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);
        Assert.Contains(
            health.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "重试当前 Feed"
                && element.Attribute("Command")?.Value.Contains("RetryFeedCommand", StringComparison.Ordinal) == true);
        Assert.Contains(
            health.Descendants(),
            element => element.Attribute("Text")?.Value.Contains("固定类别", StringComparison.Ordinal) == true);
    }
}
