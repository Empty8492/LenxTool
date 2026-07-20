using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class ControlStyleTests
{
    [Fact]
    public void DefaultButtonsUseCompactNonStretchingLayout()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Controls.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement style = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Style"
                && element.Attribute("TargetType")?.Value == "Button"
                && element.Attribute(x + "Key") is null);

        Dictionary<string, string> setters = style
            .Elements()
            .Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(
                element => element.Attribute("Property")?.Value ?? string.Empty,
                element => element.Attribute("Value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Equal("Center", setters["VerticalAlignment"]);
        Assert.Equal("36", setters["MinHeight"]);
    }

    [Fact]
    public void NewsControlsProvidePolishedComboBoxAndFloatingActionStyles()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Controls.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement comboStyle = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "CompactComboBoxStyle");
        XElement floatingStyle = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "FloatingActionButtonStyle");

        Assert.Contains(comboStyle.Descendants(), element => element.Name.LocalName == "ControlTemplate");
        Assert.Contains(
            floatingStyle.Elements(),
            element => element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value == "Width"
                && element.Attribute("Value")?.Value == "44");
    }

    [Fact]
    public void SourceFilterChipHasSelectedCheckAndFullBorderFeedback()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Controls.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement style = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "FilterChipStyle");

        Assert.Contains(
            style.Descendants(),
            element => element.Name.LocalName == "Path"
                && element.Attribute(x + "Name")?.Value == "SelectionCheck");
        Assert.Contains(
            style.Descendants(),
            element => element.Name.LocalName == "Trigger"
                && element.Attribute("Property")?.Value == "IsChecked"
                && element.Attribute("Value")?.Value == "True");
        Assert.Contains(
            style.Descendants(),
            element => element.Name.LocalName == "Setter"
                && element.Attribute("TargetName")?.Value == "Chrome"
                && element.Attribute("Property")?.Value == "BorderBrush");
    }
}
