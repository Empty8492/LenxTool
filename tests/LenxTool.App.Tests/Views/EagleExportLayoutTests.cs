using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 冻结 Eagle 的显式用户入口；设置页只能配置本机端点，Feed 行和详情
/// 必须分别携带当前条目，且页面需要暴露脱敏状态反馈。
/// </summary>
public sealed class EagleExportLayoutTests
{
    [Fact]
    public void SettingsPageExposesIndependentEagleEndpointCard()
    {
        XDocument document = XDocument.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "App.xaml"));
        XElement template = Assert.Single(
            document.Descendants()
                .Where(element =>
                    element.Name.LocalName == "DataTemplate"),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "DataType"
                && attribute.Value.Contains(
                    "SettingsViewModel",
                    StringComparison.Ordinal)));
        XElement card = Assert.Single(
            template.Descendants()
                .Where(element => element.Name.LocalName == "Border"),
            element => element.Attribute("DataContext")?.Value
                == "{Binding EagleSettings}");

        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("Text")?.Value.Contains(
                    "EndpointText",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value
                    == "{Binding SaveCommand}");
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value
                    == "{Binding TestCommand}");
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value
                    == "{Binding Status}");

        string accessibleText = string.Join(
            " ",
            card.DescendantsAndSelf()
                .Attributes()
                .Select(attribute => attribute.Value));
        Assert.Contains(
            "Eagle",
            accessibleText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "41595",
            accessibleText,
            StringComparison.Ordinal);
        Assert.Contains(
            "导出任务进入终态前保持当前资源库不变",
            accessibleText,
            StringComparison.Ordinal);
        Assert.Contains(
            "保存新端点会等待活动导出",
            accessibleText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TimelineBindsExplicitEagleExportForRowAndDetails()
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
            element =>
                (string?)element.Attribute(
                    "AutomationProperties.Name")
                == "导出该 Feed 到 Eagle");
        Assert.Equal(
            "{Binding DataContext.ExportTimelineEntryToEagleCommand, RelativeSource={RelativeSource AncestorType=UserControl}}",
            (string?)rowButton.Attribute("Command"));
        Assert.Equal(
            "{Binding}",
            (string?)rowButton.Attribute("CommandParameter"));

        XElement detailButton = Assert.Single(
            document.Descendants(presentation + "Button"),
            element =>
                (string?)element.Attribute(
                    "AutomationProperties.Name")
                == "导出当前 Feed 到 Eagle");
        Assert.Equal(
            "{Binding ExportTimelineEntryToEagleCommand}",
            (string?)detailButton.Attribute("Command"));
        Assert.Equal(
            "{Binding SelectedTimelineEntry}",
            (string?)detailButton.Attribute("CommandParameter"));
        Assert.Contains(
            detailButton.Ancestors(presentation + "WrapPanel"),
            panel => (string?)panel.Attribute("HorizontalAlignment")
                == "Right");
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element =>
                (string?)element.Attribute("Text")
                == "{Binding EagleExportStatus}");

        foreach (string binding in new[]
                 {
                     "{Binding ObsidianExportStatus}",
                     "{Binding EagleExportStatus}"
                 })
        {
            XElement status = Assert.Single(
                document.Descendants(presentation + "TextBlock"),
                element => (string?)element.Attribute("Text")
                    == binding);
            Assert.Equal(
                "Polite",
                (string?)status.Attribute(
                    "AutomationProperties.LiveSetting"));
        }
    }
}
