using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class SettingsObsidianLayoutTests
{
    [Fact]
    public void SettingsPageExposesExplicitObsidianFileExportControls()
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
                == "{Binding ObsidianSettings}");

        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("Text")?.Value.Contains(
                    "VaultRootPath",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("Text")?.Value.Contains(
                    "RelativeDirectory",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("Text")?.Value.Contains(
                    "TagsText",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("Text")?.Value.Contains(
                    "TemplateMarkdown",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "CheckBox"
                && element.Attribute("IsChecked")?.Value
                    == "{Binding IncludeSourceLink}");
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value
                    == "{Binding PickVaultFolderCommand}");
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value
                    == "{Binding SaveCommand}");
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding Status}");
        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "ProgressBar"
                && element.Attribute("Visibility")?.Value.Contains(
                    "IsBusy",
                    StringComparison.Ordinal) == true);

        string text = string.Join(
            " ",
            card.DescendantsAndSelf()
                .Attributes()
                .Select(attribute => attribute.Value));
        Assert.Contains("Markdown", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("obsidian://", text, StringComparison.OrdinalIgnoreCase);
    }
}
