using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class ProgressBindingTests
{
    [Fact]
    public void ProgressBarsUseOneWayValueBindings()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml");
        XDocument document = XDocument.Load(xamlPath);

        XElement[] progressBars = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ProgressBar")
            .ToArray();

        Assert.NotEmpty(progressBars);
        Assert.All(progressBars, progressBar =>
        {
            string? binding = progressBar.Attribute("Value")?.Value;
            Assert.NotNull(binding);
            Assert.Contains("Mode=OneWay", binding, StringComparison.Ordinal);
        });
    }
}
