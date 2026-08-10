using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 校验集成页面只使用可滚动、可键盘操作且有自动化名称的原生控件。
/// </summary>
public sealed class IntegrationLayoutTests
{
    [Fact]
    public void AdminViewUsesScrollableClosedPolicyControls()
    {
        XElement root = Load("IntegrationAdminView.xaml");
        XElement viewer = root.Descendants()
            .Single(element =>
                element.Name.LocalName == "ScrollViewer");
        string[] automationNames = root.Descendants()
            .Select(element => element.Attributes()
                .FirstOrDefault(attribute =>
                    attribute.Name.LocalName
                        == "AutomationProperties.Name")
                ?.Value)
            .OfType<string>()
            .ToArray();

        Assert.Equal(
            "Auto",
            viewer.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Contains("刷新外部集成策略", automationNames);
        Assert.Contains("发布外部集成策略", automationNames);
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "CheckBox"
                && element.Attribute("IsChecked")?.Value
                    == "{Binding IsEnabled}");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("AcceptsReturn")?.Value == "True"
                && element.Attribute("IsEnabled")?.Value
                    == "{Binding RequiresAllowedHosts}");
        Assert.Contains(
            root.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value
                    == "{Binding HostGuidance}");
    }

    [Fact]
    public void AppMapsAdminAndExposesDpapiPersonalSettings()
    {
        XElement app = LoadFromApp("App.xaml");
        XElement integrationTemplate = app.Descendants()
            .Single(element =>
                element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value
                    == "{x:Type vm:IntegrationAdminViewModel}");
        XElement settingsTemplate = app.Descendants()
            .Single(element =>
                element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value
                    == "{x:Type vm:SettingsViewModel}");
        string settings = settingsTemplate.ToString(
            SaveOptions.DisableFormatting);

        Assert.Contains(
            integrationTemplate.Elements(),
            element => element.Name.LocalName
                == "IntegrationAdminView");
        Assert.Contains(
            "个人外部集成凭据",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "PasswordBoxAssistant.BoundPassword",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "未接通类型不会显示",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeleteLegacyCredentialCommand",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "删除旧版占位凭据",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeleteSpecifiedLegacyCredentialCommand",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "只删除，不保存或测试连接",
            settings,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CredentialOutput",
            settings,
            StringComparison.Ordinal);
    }

    private static XElement Load(string name) =>
        XElement.Load(Path.Combine(
            ProjectRoot(),
            "src",
            "LenxTool.App",
            "Views",
            name));

    private static XElement LoadFromApp(string name) =>
        XElement.Load(Path.Combine(
            ProjectRoot(),
            "src",
            "LenxTool.App",
            name));

    private static string ProjectRoot()
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(
                   Path.Combine(directory, "LenxTool.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Repository root was not found.");
        }
        return directory;
    }
}
