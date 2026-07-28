using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 用结构测试冻结发现页的可访问性、窄窗布局和发布确认边界。
/// </summary>
public sealed class FeedDiscoveryLayoutTests
{
    [Fact]
    public void DiscoveryTemplateExposesAccessibleSearchAndPublishStates()
    {
        XDocument app = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        XElement template = app.Descendants().Single(element =>
            element.Name.LocalName == "DataTemplate"
            && element.Attribute("DataType")?.Value.Contains(
                "FeedAdminViewModel",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "发现"
                && element.Descendants().Any(child =>
                    child.Name.LocalName == "FeedDiscoveryView"
                    && child.Attribute("DataContext")?.Value.Contains(
                        "UnifiedDiscovery",
                        StringComparison.Ordinal) == true));

        XDocument view = XDocument.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "FeedDiscoveryView.xaml"));
        foreach (string automationName in new[]
        {
            "发现输入识别类型",
            "统一发现输入",
            "提交统一发现",
            "取消统一发现",
            "重试统一发现",
            "统一发现空状态",
            "统一发现候选列表",
            "准备加入共享目录",
            "发布确认面板",
            "发布分类",
            "发布刷新策略",
            "发布视图策略",
            "发布全文策略",
            "确认发布设置",
            "确认加入共享目录",
            "取消发布确认",
            "刷新发布目录"
        })
        {
            Assert.Contains(view.Descendants(), element =>
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "AutomationProperties.Name"
                    && attribute.Value == automationName));
        }

        Assert.Contains(view.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.LiveSetting"
                && attribute.Value == "Polite")
            && element.Attribute("Text")?.Value.Contains(
                "Status",
                StringComparison.Ordinal) == true);
        foreach (string command in new[]
        {
            "SearchCommand",
            "CancelCommand",
            "RetryCommand"
        })
        {
            Assert.Contains(view.Descendants(), element =>
                element.Attribute("Command")?.Value.Contains(
                    command,
                    StringComparison.Ordinal) == true);
        }

        foreach (string command in new[]
        {
            "PreparePublishCommand",
            "PublishCommand",
            "CancelPublishCommand",
            "RefreshCatalogCommand"
        })
        {
            Assert.Contains(view.Descendants(), element =>
                element.Attribute("Command")?.Value.Contains(
                    command,
                    StringComparison.Ordinal) == true);
        }

        Assert.Contains(view.Descendants(), element =>
            element.Name.LocalName == "CheckBox"
            && element.Attribute("IsChecked")?.Value.Contains(
                "IsPublishConfirmed",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DiscoveryViewKeepsNarrowWindowScrollableAndAdminOnly()
    {
        XDocument view = XDocument.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "FeedDiscoveryView.xaml"));
        XElement scroller = Assert.Single(
            view.Root!.Elements(),
            element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal(
            "Auto",
            scroller.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Equal(
            "Auto",
            scroller.Attribute("VerticalScrollBarVisibility")?.Value);

        string appSource = File.ReadAllText(
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "LenxTool.App",
                    "App.xaml.cs")));
        Assert.DoesNotContain(
            "new(\"feed-discovery\"",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"feed-admin\"",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "feedAdmin, AdminOnly: true",
            appSource,
            StringComparison.Ordinal);
    }
}
