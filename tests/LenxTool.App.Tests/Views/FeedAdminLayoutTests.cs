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
            "Feed 地址",
            "Feed 显示名称",
            "Feed 站点地址",
            "Feed 分类",
            "Feed 视图类型",
            "Feed 刷新间隔",
            "Feed 排序",
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
}
