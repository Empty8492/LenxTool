using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 冻结 Zotero 个人库专用入口及其可访问标签，避免退回通用任意端点表单。
/// </summary>
public sealed class ZoteroExportLayoutTests
{
    [Fact]
    public void SettingsPageExposesDedicatedPersonalLibraryCard()
    {
        XDocument document = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        XElement template = Assert.Single(
            document.Descendants()
                .Where(element => element.Name.LocalName == "DataTemplate"),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "DataType"
                && attribute.Value.Contains(
                    "SettingsViewModel",
                    StringComparison.Ordinal)));
        XElement card = Assert.Single(
            template.Descendants()
                .Where(element => element.Name.LocalName == "Border"),
            element => element.Attribute("DataContext")?.Value
                == "{Binding ZoteroSettings}");

        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("Text")?.Value.Contains(
                    "UserIdText",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && element.Attribute("SelectedItem")?.Value.Contains(
                    "SelectedItemType",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "PasswordBox"
                && element.Attributes().Any(attribute =>
                    attribute.Value.Contains(
                        "CredentialInput",
                        StringComparison.Ordinal)));
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "CheckBox"
                && element.Attribute("IsChecked")?.Value
                    == "{Binding IncludeSummaryNote}");
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "CheckBox"
                && element.Attribute("IsChecked")?.Value
                    == "{Binding UploadFirstImageAttachment}");

        foreach (string command in new[]
                 {
                     "{Binding SaveCommand}",
                     "{Binding TestCommand}",
                     "{Binding DeleteCredentialCommand}"
                 })
        {
            Assert.Contains(
                card.Descendants(),
                element => element.Name.LocalName == "Button"
                    && element.Attribute("Command")?.Value == command);
        }

        string accessibleText = string.Join(
            " ",
            card.DescendantsAndSelf()
                .Attributes()
                .Select(attribute => attribute.Value));
        Assert.Contains("Zotero", accessibleText, StringComparison.Ordinal);
        Assert.Contains("个人库", accessibleText, StringComparison.Ordinal);
        Assert.Contains("默认关闭", accessibleText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EndpointText",
            accessibleText,
            StringComparison.Ordinal);
    }
}
