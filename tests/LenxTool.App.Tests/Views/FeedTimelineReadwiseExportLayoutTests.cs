using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 冻结 Readwise 只由用户显式触发，并在发送前展示与实际 summary 完全一致的裁剪预览。
/// </summary>
public sealed class FeedTimelineReadwiseExportLayoutTests
{
    [Fact]
    public void TimelineBindsReadwiseExportAndExactExcerptPreview()
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
                == "导出该 Feed 到 Readwise");
        Assert.Equal(
            "{Binding DataContext.ExportTimelineEntryToReadwiseCommand, RelativeSource={RelativeSource AncestorType=UserControl}}",
            rowButton.Attribute("Command")?.Value);
        Assert.Equal("{Binding}", rowButton.Attribute("CommandParameter")?.Value);

        XElement detailButton = Assert.Single(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute(
                "AutomationProperties.Name")
                == "导出当前 Feed 到 Readwise");
        Assert.Equal(
            "{Binding ExportTimelineEntryToReadwiseCommand}",
            detailButton.Attribute("Command")?.Value);
        Assert.Equal(
            "{Binding SelectedTimelineEntry}",
            detailButton.Attribute("CommandParameter")?.Value);

        Assert.Single(
            document.Descendants(presentation + "TextBlock"),
            element => element.Attribute("Text")?.Value
                == "{Binding ReadwiseExportStatus}");
        XElement preview = Assert.Single(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute(
                "AutomationProperties.Name")
                == "Readwise 发送摘要预览");
        Assert.Equal(
            "{Binding ReadwiseExportPreview, Mode=OneWay}",
            preview.Attribute("Text")?.Value);
        Assert.Equal("True", preview.Attribute("IsReadOnly")?.Value);
    }
}
