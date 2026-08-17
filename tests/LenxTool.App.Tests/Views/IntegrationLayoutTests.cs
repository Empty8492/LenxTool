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
        string[] textBindings = root.Descendants()
            .Where(element => element.Name.LocalName == "TextBox")
            .Select(element => element.Attribute("Text")?.Value)
            .OfType<string>()
            .ToArray();
        Assert.Contains(
            "{Binding TrustedPrivateEndpointsText, UpdateSourceTrigger=PropertyChanged}",
            textBindings);
        Assert.Contains(
            "{Binding AllowedResourcesText, UpdateSourceTrigger=PropertyChanged}",
            textBindings);
        Assert.Contains(
            "{Binding AllowedLoopbackHttpPortsText, UpdateSourceTrigger=PropertyChanged}",
            textBindings);
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
            "Readeck、Outline、qBittorrent 与 Webhook 使用下方专用卡",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            settingsTemplate.Descendants(),
            element => element.Name.LocalName
                == "ManagedIntegrationSettingsView"
                && element.Attribute("DataContext")?.Value
                    == "{Binding ManagedIntegrations}");
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

    [Fact]
    public void ManagedIntegrationCardsExposeClosedProviderSpecificControls()
    {
        XElement root = Load("ManagedIntegrationSettingsView.xaml");
        string xaml = root.ToString(SaveOptions.DisableFormatting);
        string[] automationNames = root.Descendants()
            .Select(element => element.Attributes()
                .FirstOrDefault(attribute =>
                    attribute.Name.LocalName
                        == "AutomationProperties.Name")
                ?.Value)
            .OfType<string>()
            .ToArray();

        Assert.Contains("Readeck 实例 HTTPS 根地址", automationNames);
        Assert.Contains("Outline Collection ID", automationNames);
        Assert.Contains("qBittorrent 保存分类", automationNames);
        Assert.Contains("受控 Webhook HTTPS 地址", automationNames);
        Assert.Contains("qBittorrent 5.2+ / WebAPI 2.14.1+", xaml);
        Assert.Contains("首版始终创建个人草稿", xaml);
        Assert.Contains("API key 会通过本机 TCP 明文传输", xaml);
        Assert.Contains("Idempotency-Key", xaml);
        Assert.Contains("PasswordBoxAssistant.BoundPassword", xaml);
        Assert.DoesNotContain("CredentialOutput", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TimelineRequiresExplicitQBittorrentConfirmation()
    {
        XElement root = Load("FeedTimelineBrowserView.xaml");
        string xaml = root.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("PrepareTimelineEntryForQBittorrentCommand", xaml);
        Assert.Contains("HasPendingQBittorrentExport", xaml);
        Assert.Contains("ConfirmTimelineEntryToQBittorrentCommand", xaml);
        Assert.Contains("CancelTimelineEntryToQBittorrentCommand", xaml);
        Assert.Contains("确认启动下载", xaml);
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
