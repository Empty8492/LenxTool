using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class AutomationAdminLayoutTests
{
    [Fact]
    public void PageExposesGraphicalRuleControlsAndReadOnlySimulationWarning()
    {
        XDocument document = LoadView();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        string[] automationNames = document
            .Descendants()
            .Attributes()
            .Where(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name")
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.Contains("条件字段", automationNames);
        Assert.Contains("条件操作符", automationNames);
        Assert.Contains("条件值", automationNames);
        Assert.Contains("规则动作", automationNames);
        Assert.Contains("只读模拟规则", automationNames);
        Assert.Contains("发布自动化规则", automationNames);
        Assert.Contains(
            document.Descendants(presentation + "ComboBox"),
            element => element.Attribute("DisplayMemberPath")?.Value == "Label");
        string text = string.Concat(
            document.DescendantNodes().OfType<XText>().Select(item => item.Value))
            + string.Concat(
                document.Descendants()
                    .Attributes("Text")
                    .Select(attribute => attribute.Value));
        Assert.Contains("不支持脚本或自定义请求", text, StringComparison.Ordinal);
        Assert.Contains("不会调用 AI、投递媒体或发送通知", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            document.Descendants(presentation + "TextBox"),
            element => element.Attributes().Any(attribute =>
                attribute.Value.Contains("脚本", StringComparison.Ordinal)));
    }

    [Fact]
    public void AppMapsAutomationAdminViewModelToDedicatedView()
    {
        XDocument app = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement template = Assert.Single(
            app.Descendants(presentation + "DataTemplate"),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "DataType"
                && attribute.Value.Contains(
                    "AutomationAdminViewModel",
                    StringComparison.Ordinal)));

        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "AutomationAdminView");
    }

    private static XDocument LoadView() => XDocument.Load(
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "AutomationAdminView.xaml"));
}
