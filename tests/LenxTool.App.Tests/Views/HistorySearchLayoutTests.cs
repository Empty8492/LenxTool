using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class HistorySearchLayoutTests
{
    [Fact]
    public void SearchPageExposesCombinedFiltersPagingAndResultNavigation()
    {
        XDocument document = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        XDocument filterBar = XDocument.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "HistorySearchFilterBar.xaml"));
        XElement template = Assert.Single(
            document.Descendants()
                .Where(element => element.Name.LocalName == "DataTemplate"),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "DataType"
                && attribute.Value.Contains(
                    "HistoryViewModel",
                    StringComparison.Ordinal)));
        string[] automationNames = template
            .Descendants()
            .Concat(filterBar.Descendants())
            .Attributes()
            .Where(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name")
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.Contains("搜索内容类型", automationNames);
        Assert.Contains("搜索起始日期", automationNames);
        Assert.Contains("搜索截止日期", automationNames);
        Assert.Contains("搜索 Feed 分类", automationNames);
        Assert.Contains("搜索 Feed 来源", automationNames);
        Assert.Contains("搜索标签", automationNames);
        Assert.Contains("仅搜索收藏", automationNames);
        Assert.Contains("加载更多搜索结果", automationNames);
        Assert.Contains("打开搜索结果", automationNames);
        Assert.Contains(
            template.Descendants()
                .Where(element => element.Name.LocalName == "TabControl"),
            element => element.Attribute("SelectedIndex")?.Value
                .Contains(
                    "SelectedHistoryTabIndex",
                    StringComparison.Ordinal) == true);
    }
}
