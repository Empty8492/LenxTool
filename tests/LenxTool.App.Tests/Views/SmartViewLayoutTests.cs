using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class SmartViewLayoutTests
{
    [Fact]
    public void OrdinaryTimelineExposesReadOnlyPublishedViewSelection()
    {
        XElement filters = Load("FeedTimelineFiltersView.xaml");

        Assert.Contains(
            filters.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && element.Attribute("AutomationProperties.Name")?.Value
                    == "选择已发布智能视图"
                && element.Attribute("ItemsSource")?.Value
                    == "{Binding TimelineSmartViews}"
                && element.Attribute("SelectedItem")?.Value
                    == "{Binding SelectedTimelineSmartView}");
        Assert.Contains(
            filters.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value
                    == "使用已发布智能视图"
                && element.Attribute("Command")?.Value
                    == "{Binding ApplyTimelineSmartViewCommand}");
        Assert.DoesNotContain(
            filters.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Content")?.Value is
                    "发布新视图" or "发布新版本" or "删除");
    }

    [Fact]
    public void AdminEditorUsesOnlyClosedFilterControlsAndConfirmation()
    {
        XElement view = Load("SmartViewAdminView.xaml");
        string[] requiredControls =
        [
            "智能视图名称",
            "智能视图排序",
            "智能视图分类",
            "智能视图 Feed",
            "智能视图内容类型",
            "智能视图阅读状态",
            "智能视图最近天数",
            "智能视图仅看收藏",
            "智能视图关键词",
            "发布共享智能视图",
            "确认删除共享智能视图"
        ];

        Assert.All(
            requiredControls,
            name => Assert.Contains(
                view.Descendants(),
                element => element
                    .Attribute("AutomationProperties.Name")?.Value == name));
        Assert.DoesNotContain(
            view.Descendants(),
            element => element.Name.LocalName is
                "WebBrowser" or "PasswordBox");
        string bindings = string.Join(
            "\n",
            view.DescendantsAndSelf()
                .Attributes()
                .Select(attribute => attribute.Value));
        Assert.DoesNotContain("FilterJson", bindings, StringComparison.Ordinal);
        Assert.DoesNotContain("Script", bindings, StringComparison.Ordinal);
        Assert.DoesNotContain("OriginalUrl", bindings, StringComparison.Ordinal);
        Assert.Contains(
            view.Descendants(),
            element => element.Name.LocalName == "DataTrigger"
                && element.Attribute("Binding")?.Value
                    == "{Binding IsDeletePending}"
                && element.Attribute("Value")?.Value == "True");
    }

    [Fact]
    public void AppMapsAdminViewModelToDedicatedNativeView()
    {
        XElement app = Load("App.xaml");

        Assert.Contains(
            app.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "DataType"
                    && attribute.Value
                        == "{x:Type vm:SmartViewAdminViewModel}")
                && element.Descendants().Any(child =>
                    child.Name.LocalName == "SmartViewAdminView"));
    }

    private static XElement Load(string name) =>
        XElement.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            name));
}
