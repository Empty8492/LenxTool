using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class ToolsLayoutTests
{
    [Fact]
    public void ToolsPageExposesAccessibleCancelableJsonDiffWorkspace()
    {
        XElement app = Load("src", "LenxTool.App", "App.xaml");
        XElement template = app.Descendants()
            .Single(element =>
                element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value
                    == "{x:Type vm:ToolsViewModel}");
        XElement view = Load("src", "LenxTool.App", "Views", "ToolsView.xaml");
        string viewText = view.ToString(SaveOptions.DisableFormatting);
        string[] automationNames = view.Descendants()
            .Attributes()
            .Where(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name")
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "ToolsView");
        Assert.Contains("左侧 JSON", automationNames);
        Assert.Contains("右侧 JSON", automationNames);
        Assert.Contains("比较 JSON 结构", automationNames);
        Assert.Contains("取消 JSON 比较", automationNames);
        Assert.Contains("交换左右 JSON", automationNames);
        Assert.Contains("JSON Diff 内容滚动区", automationNames);
        XElement diffScrollViewer = view.Descendants()
            .Single(element =>
                element.Name.LocalName == "ScrollViewer"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName
                        == "AutomationProperties.Name"
                    && attribute.Value == "JSON Diff 内容滚动区"));
        Assert.Equal(
            "Auto",
            diffScrollViewer.Attribute(
                "VerticalScrollBarVisibility")?.Value);
        Assert.Equal(
            "Auto",
            diffScrollViewer.Attribute(
                "HorizontalScrollBarVisibility")?.Value);
        Assert.Contains(
            "Text=\"{Binding LeftJson, UpdateSourceTrigger=PropertyChanged}\"",
            viewText,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding RightJson, UpdateSourceTrigger=PropertyChanged}\"",
            viewText,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding Differences}\"",
            viewText,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding CompareJsonCommand}\"",
            viewText,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding CancelJsonDiffCommand}\"",
            viewText,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Polite\"",
            viewText,
            StringComparison.Ordinal);
        Assert.Contains(
            "数量上限 500",
            viewText,
            StringComparison.Ordinal);
        Assert.Contains(
            "路径 1,024/256 KiB",
            viewText,
            StringComparison.Ordinal);
    }

    private static XElement Load(params string[] relativeParts) =>
        XElement.Load(Path.Combine([ProjectRoot(), .. relativeParts]));

    private static string ProjectRoot()
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "LenxTool.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Repository root was not found.");
        }

        return directory;
    }
}
