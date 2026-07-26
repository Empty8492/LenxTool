using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class SettingsStorageLayoutTests
{
    [Fact]
    public void SettingsPageExposesStorageUsagePreviewAndConfirmationControls()
    {
        XDocument document = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        XDocument storageCard = XDocument.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "SettingsStorageCard.xaml"));
        XElement template = Assert.Single(
            document.Descendants()
                .Where(element => element.Name.LocalName == "DataTemplate"),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "DataType"
                && attribute.Value.Contains(
                    "SettingsViewModel",
                    StringComparison.Ordinal)));
        string[] automationNames = template
            .Descendants()
            .Concat(storageCard.Descendants())
            .Attributes()
            .Where(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name")
            .Select(attribute => attribute.Value)
            .ToArray();
        string text = string.Join(
            " ",
            template.Descendants()
                .Concat(storageCard.Descendants())
                .Attributes()
                .Where(attribute => attribute.Name.LocalName == "Text")
                .Select(attribute => attribute.Value));

        Assert.Contains("本地存储占用", automationNames);
        Assert.Contains("刷新存储占用", automationNames);
        Assert.Contains("预览安全清理", automationNames);
        Assert.Contains("确认安全清理", automationNames);
        Assert.Contains("取消安全清理", automationNames);
        Assert.Contains("180 天", text, StringComparison.Ordinal);
    }
}
