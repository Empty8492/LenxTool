using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class DashboardLayoutTests
{
    [Fact]
    public void DashboardBindsLiveStatusesAndFavoriteCount()
    {
        XElement template = LoadDashboardTemplate();

        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding NewsStatus}");
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding TrendStatus}");
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding FavoriteSummary}");
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding DataStatus}");
        Assert.DoesNotContain(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value.Contains("08:32", StringComparison.Ordinal) == true);
    }

    private static XElement LoadDashboardTemplate()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml");
        XDocument document = XDocument.Load(xamlPath);
        return Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value.Contains(
                    "DashboardViewModel",
                    StringComparison.Ordinal) == true);
    }
}
