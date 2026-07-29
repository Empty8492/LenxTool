using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class ProgressBindingTests
{
    [Fact]
    public void ProgressBarsUseOneWayValueBindingsOrExplicitIndeterminateMode()
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
            if (string.Equals(
                    progressBar.Attribute("IsIndeterminate")?.Value,
                    "True",
                    StringComparison.OrdinalIgnoreCase))
            {
                Assert.Null(progressBar.Attribute("Value"));
                return;
            }

            string? binding = progressBar.Attribute("Value")?.Value;
            Assert.NotNull(binding);
            Assert.Contains("Mode=OneWay", binding, StringComparison.Ordinal);
        });
    }
}
