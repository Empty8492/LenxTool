using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class ControlStyleTests
{
    // 共享列表样式必须覆盖普通、ListView 与分页派生控件。
    private static readonly string[] PixelVirtualizedListTargetTypes =
    [
        "{x:Type ListBox}",
        "{x:Type ListView}",
        "{x:Type controls:PagedListBox}"
    ];

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

    [Fact]
    public void SharedSelectionControlsKeepRequiredTemplateParts()
    {
        XDocument controls = LoadFixture("Controls.xaml");
        XDocument dateControls = LoadFixture("DateControls.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement tabControl = FindStyle(controls, x, "SegmentedTabControlStyle");
        XElement datePicker = FindStyle(dateControls, x, "CompactDatePickerStyle");
        XElement dateTextBox = FindStyle(
            dateControls,
            x,
            "CompactDatePickerTextBoxStyle");

        AssertNamedParts(tabControl, x, "PART_SelectedContentHost");
        AssertNamedParts(
            datePicker,
            x,
            "PART_Root",
            "PART_TextBox",
            "PART_Button",
            "PART_Popup");
        AssertNamedParts(
            dateTextBox,
            x,
            "PART_ContentElement",
            "PART_Watermark");
        Assert.NotNull(FindStyle(dateControls, x, "CompactCalendarStyle"));
        Assert.NotNull(FindStyle(dateControls, x, "CompactCalendarDayButtonStyle"));
        Assert.NotNull(FindStyle(dateControls, x, "CompactCalendarButtonStyle"));
    }

    [Fact]
    public void SharedSelectionControlsExposeCompleteInteractionFeedback()
    {
        XDocument controls = LoadFixture("Controls.xaml");
        XDocument dateControls = LoadFixture("DateControls.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        AssertTriggers(
            FindStyle(controls, x, "SegmentedTabItemStyle"),
            "IsMouseOver",
            "IsMouseCaptureWithin",
            "IsSelected",
            "IsKeyboardFocused",
            "IsEnabled");
        AssertTriggers(
            FindStyle(dateControls, x, "CompactDatePickerStyle"),
            "IsMouseOver",
            "IsKeyboardFocusWithin",
            "IsDropDownOpen",
            "Validation.HasError",
            "IsEnabled");
        AssertTriggers(
            FindStyle(controls, x, "CompactCheckBoxStyle"),
            "IsMouseOver",
            "IsPressed",
            "IsChecked",
            "IsKeyboardFocused",
            "Validation.HasError",
            "IsEnabled");
        AssertTriggers(
            FindStyle(controls, x, "CompactComboBoxStyle"),
            "IsMouseOver",
            "IsMouseCaptureWithin",
            "IsDropDownOpen",
            "IsKeyboardFocusWithin",
            "Validation.HasError",
            "IsEnabled");
    }

    [Fact]
    public void VirtualizedListsAndDropdownsUsePixelScrollUnits()
    {
        XDocument app = LoadFixture("App.xaml");
        XDocument controls = LoadFixture("Controls.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        // 主列表、分页列表和下拉框都必须显式保留虚拟化与像素滚动。
        XElement sharedListStyle = Assert.Single(
            app.Descendants(),
            element => element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value
                    == "PixelVirtualizedListStyle");
        AssertPixelVirtualization(sharedListStyle);
        Assert.All(
            PixelVirtualizedListTargetTypes,
            targetType =>
            {
                XElement style = Assert.Single(
                    app.Descendants(),
                    element => element.Name.LocalName == "Style"
                        && element.Attribute("TargetType")?.Value
                            == targetType
                        && element.Attribute(x + "Key") is null);
                Assert.Equal(
                    "{StaticResource PixelVirtualizedListStyle}",
                    style.Attribute("BasedOn")?.Value);
            });
        XElement defaultComboBox = Assert.Single(
            app.Descendants(),
            element => element.Name.LocalName == "Style"
                && element.Attribute("TargetType")?.Value
                    == "{x:Type ComboBox}");
        AssertPixelVirtualization(defaultComboBox);
        AssertPixelVirtualization(
            FindStyle(controls, x, "CompactComboBoxStyle"));
    }

    private static XElement FindStyle(
        XDocument document,
        XNamespace x,
        string key) =>
        Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == key);

    private static XDocument LoadFixture(string fileName) =>
        XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            fileName));

    private static void AssertNamedParts(
        XElement style,
        XNamespace x,
        params string[] names)
    {
        Assert.All(
            names,
            name => Assert.Contains(
                style.Descendants(),
                element => element.Attribute(x + "Name")?.Value == name));
    }

    private static void AssertTriggers(
        XElement style,
        params string[] properties)
    {
        Assert.All(
            properties,
            property => Assert.Contains(
                style.Descendants(),
                element => element.Name.LocalName == "Trigger"
                    && element.Attribute("Property")?.Value == property));
    }

    private static void AssertPixelVirtualization(XElement style)
    {
        Assert.Contains(
            style.Elements(),
            element => element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value
                    == "VirtualizingPanel.IsVirtualizing"
                && element.Attribute("Value")?.Value == "True");
        Assert.Contains(
            style.Elements(),
            element => element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value
                    == "VirtualizingPanel.ScrollUnit"
                && element.Attribute("Value")?.Value == "Pixel");
        Assert.Contains(
            style.Elements(),
            element => element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value
                    == "VirtualizingPanel.VirtualizationMode"
                && element.Attribute("Value")?.Value == "Recycling");
        Assert.Contains(
            style.Elements(),
            element => element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value
                    == "VirtualizingPanel.CacheLength"
                && element.Attribute("Value")?.Value == "1,1");
        Assert.Contains(
            style.Elements(),
            element => element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value
                    == "VirtualizingPanel.CacheLengthUnit"
                && element.Attribute("Value")?.Value == "Page");
    }
}
