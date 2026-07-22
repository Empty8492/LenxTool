using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class AccountLayoutTests
{
    [Fact]
    public void SettingsTemplateExposesAccountLoginLogoutAndQuotaControls()
    {
        XDocument document = LoadFixture("App.xaml");
        XElement settingsTemplate = document.Descendants()
            .Single(element => element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value.Contains("SettingsViewModel", StringComparison.Ordinal) == true);

        Assert.Contains(settingsTemplate.Descendants(), element =>
            element.Name.LocalName == "TextBlock" && element.Attribute("Text")?.Value == "共享账号");
        Assert.Contains(settingsTemplate.Descendants(), element =>
            element.Name.LocalName == "TextBox"
            && element.Attribute("Text")?.Value.Contains("AccountUsernameInput", StringComparison.Ordinal) == true
            && HasAutomationName(element));
        Assert.Contains(settingsTemplate.Descendants(), element =>
            element.Name.LocalName == "PasswordBox"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "PasswordBoxAssistant.BoundPassword"
                && attribute.Value.Contains("AccountPasswordInput", StringComparison.Ordinal))
            && HasAutomationName(element));

        foreach (string command in new[] { "LoginCommand", "LogoutCommand", "RefreshAccountCommand" })
        {
            Assert.Contains(settingsTemplate.Descendants(), element =>
                element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value.Contains(command, StringComparison.Ordinal) == true
                && HasAutomationName(element));
        }
        Assert.Contains(settingsTemplate.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value.Contains("AccountQuotaSummary", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void SidebarBindsLiveAccountStatusInsteadOfStaticPlaceholder()
    {
        XDocument document = LoadFixture("MainWindow.xaml");
        XElement status = document.Descendants()
            .Single(element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value.Contains("CloudAccountStatus", StringComparison.Ordinal) == true);

        Assert.NotNull(status);
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "云服务未登录 · 可离线使用");
    }

    private static bool HasAutomationName(XElement element) => element.Attributes().Any(attribute =>
        attribute.Name.LocalName == "AutomationProperties.Name"
        && !string.IsNullOrWhiteSpace(attribute.Value));

    private static XDocument LoadFixture(string fileName) => XDocument.Load(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
