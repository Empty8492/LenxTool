using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 冻结 Zotero 只由用户显式触发的行级、详情级入口及封闭状态反馈。
/// </summary>
public sealed class FeedTimelineZoteroExportLayoutTests
{
    [Fact]
    public void TimelineBindsExplicitZoteroExportForRowAndDetails()
    {
        XDocument document = XDocument.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "FeedTimelineBrowserView.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement rowButton = Assert.Single(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute(
                "AutomationProperties.Name")
                == "导出该 Feed 到 Zotero");
        Assert.Equal(
            "{Binding DataContext.ExportTimelineEntryToZoteroCommand, RelativeSource={RelativeSource AncestorType=UserControl}}",
            rowButton.Attribute("Command")?.Value);
        Assert.Equal(
            "{Binding}",
            rowButton.Attribute("CommandParameter")?.Value);

        XElement detailButton = Assert.Single(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute(
                "AutomationProperties.Name")
                == "导出当前 Feed 到 Zotero");
        Assert.Equal(
            "{Binding ExportTimelineEntryToZoteroCommand}",
            detailButton.Attribute("Command")?.Value);
        Assert.Equal(
            "{Binding SelectedTimelineEntry}",
            detailButton.Attribute("CommandParameter")?.Value);

        Assert.Single(
            document.Descendants(presentation + "TextBlock"),
            element => element.Attribute("Text")?.Value
                    == "{Binding ZoteroExportStatus}");
    }
}
