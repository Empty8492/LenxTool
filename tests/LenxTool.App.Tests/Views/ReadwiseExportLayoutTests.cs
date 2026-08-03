using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 冻结 Readwise 在通用凭据卡中的固定官方目标提示，避免高价值令牌被发送到任意端点。
/// </summary>
public sealed class ReadwiseExportLayoutTests
{
    [Fact]
    public void SettingsPageLocksReadwiseTargetToOfficialHost()
    {
        XDocument document = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        XElement card = Assert.Single(
            document.Descendants()
                .Where(element => element.Name.LocalName == "Border"),
            element => element.Attribute("DataContext")?.Value
                == "{Binding IntegrationSettings}");

        XElement targetId = Assert.Single(
            card.Descendants()
                .Where(element => element.Name.LocalName == "TextBox"),
            element => (string?)element.Attribute(
                "AutomationProperties.Name")
                == "个人外部集成本机目标标识");
        XElement endpoint = Assert.Single(
            card.Descendants()
                .Where(element => element.Name.LocalName == "TextBox"),
            element => (string?)element.Attribute(
                "AutomationProperties.Name")
                == "个人外部集成目标地址");

        Assert.Equal(
            "{Binding IsFixedReadwiseTarget}",
            targetId.Attribute("IsReadOnly")?.Value);
        Assert.Equal(
            "{Binding IsFixedReadwiseTarget}",
            endpoint.Attribute("IsReadOnly")?.Value);
        string accessibleText = string.Join(
            " ",
            card.DescendantsAndSelf()
                .Attributes()
                .Select(attribute => attribute.Value));
        Assert.Contains("readwise.io", accessibleText, StringComparison.Ordinal);
        Assert.Contains("Reader", accessibleText, StringComparison.Ordinal);
        Assert.Contains("DPAPI", accessibleText, StringComparison.Ordinal);
    }
}
