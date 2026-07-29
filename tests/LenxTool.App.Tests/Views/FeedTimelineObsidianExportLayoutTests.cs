using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 冻结 Feed 行级 Obsidian 导出入口；按钮只能显式执行并携带当前行。
/// </summary>
public sealed class FeedTimelineObsidianExportLayoutTests
{
    [Fact]
    public void TimelineBindsExplicitObsidianExportToCurrentRowAndStatus()
    {
        XDocument document = XDocument.Load(
            FindRepositoryFile(
                "src",
                "LenxTool.App",
                "Views",
                "FeedTimelineBrowserView.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement button = Assert.Single(
            document.Descendants(presentation + "Button"),
            element =>
                (string?)element.Attribute(
                    "AutomationProperties.Name")
                == "导出该 Feed 到 Obsidian");

        Assert.Equal(
            "{Binding DataContext.ExportTimelineEntryToObsidianCommand, RelativeSource={RelativeSource AncestorType=UserControl}}",
            (string?)button.Attribute("Command"));
        Assert.Equal(
            "{Binding}",
            (string?)button.Attribute("CommandParameter"));
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element =>
                (string?)element.Attribute("Text")
                == "{Binding ObsidianExportStatus}");
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(
            "无法定位仓库中的 XAML 文件。",
            Path.Combine(parts));
    }
}
