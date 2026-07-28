using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.App.Views;

namespace LenxTool.App.Tests.Views;

/// <summary>
/// 在真实 WPF 资源和布局系统中验证发现页的键盘语义、缩放和主题切换。
/// </summary>
[Collection(WpfRuntimeGroup.Name)]
public sealed class FeedDiscoveryWpfRuntimeTests
{
    [Fact]
    public void DiscoveryViewKeepsNativeAutomationAndLayoutAcrossThemes()
    {
        Exception? failure = null;
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                var themeService = new ThemeService();
                try
                {
                    themeService.ApplyTheme(useDarkTheme: false);
                    var view = new FeedDiscoveryView
                    {
                        DataContext = RuntimeModel()
                    };
                    window = new Window
                    {
                        Width = 900,
                        Height = 620,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = view
                    };
                    window.Show();
                    window.UpdateLayout();

                    TextBox input = FindDescendant<TextBox>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "统一发现输入");
                    Button submit = FindDescendant<Button>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "提交统一发现");
                    ItemsControl candidates = FindDescendant<ItemsControl>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "统一发现候选列表");

                    Assert.Equal(
                        AutomationControlType.Edit,
                        CreatePeer(input).GetAutomationControlType());
                    Assert.Equal(
                        AutomationControlType.Button,
                        CreatePeer(submit).GetAutomationControlType());
                    Assert.Single(candidates.Items);
                    Assert.True(input.Focus());
                    Assert.True(input.ActualHeight >= 36);
                    Assert.True(submit.ActualHeight >= 36);

                    string lightBackground = input.Background.ToString(
                        CultureInfo.InvariantCulture);
                    themeService.ApplyTheme(useDarkTheme: true);
                    window.UpdateLayout();
                    string darkBackground = input.Background.ToString(
                        CultureInfo.InvariantCulture);
                    Assert.NotEqual(lightBackground, darkBackground);

                    // 以双倍布局尺寸模拟 200% DPI，候选卡仍由页面滚动容器承载。
                    window.Width = 1800;
                    window.Height = 1240;
                    view.LayoutTransform = new ScaleTransform(2d, 2d);
                    window.UpdateLayout();
                    Assert.True(view.ActualWidth >= 650);
                    Assert.True(candidates.ActualWidth > 0);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    themeService.ApplyTheme(useDarkTheme: false);
                    window?.Close();
                }
            },
            TimeSpan.FromSeconds(15),
            () => "统一发现页 WPF 运行时验收超时。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static RuntimeDiscoveryModel RuntimeModel()
    {
        var search = new RelayCommand(() => { });
        var cancel = new RelayCommand(() => { });
        var retry = new RelayCommand(() => { });
        return new(
            "发现订阅",
            "只读候选预览",
            "reader",
            "关键词",
            "已找到 1 个候选。",
            false,
            true,
            false,
            false,
            search,
            cancel,
            retry,
            new[]
            {
                new FeedDiscoveryCandidateViewModel(
                    "示例订阅",
                    "https://feeds.example/feed.xml",
                    "https://feeds.example/",
                    "RSS 2.0",
                    "健康",
                    "已知目录",
                    "未发现风险提示",
                    "2026-07-28 16:00",
                    [
                        new(
                            "最近条目",
                            "2026-07-28 15:30")
                    ])
            });
    }

    /// <summary>
    /// 运行时夹具保留可写输入属性，以符合 TextBox 默认双向绑定契约。
    /// </summary>
    private sealed record RuntimeDiscoveryModel(
        string Title,
        string Subtitle,
        string InputValue,
        string QueryKindLabel,
        string Status,
        bool IsBusy,
        bool HasCandidates,
        bool ShowEmptyState,
        bool CanShowRetry,
        RelayCommand SearchCommand,
        RelayCommand CancelCommand,
        RelayCommand RetryCommand,
        IReadOnlyList<FeedDiscoveryCandidateViewModel> Candidates)
    {
        public string Input { get; set; } = InputValue;
    }

    private static AutomationPeer CreatePeer(UIElement element) =>
        UIElementAutomationPeer.CreatePeerForElement(element)
        ?? throw new InvalidOperationException(
            $"{element.GetType().Name} 没有创建自动化 Peer。");

    private static T FindDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (int index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match)) return match;
            try
            {
                return FindDescendant(child, predicate);
            }
            catch (InvalidOperationException)
            {
                // 当前子树没有目标控件，继续搜索相邻分支。
            }
        }
        throw new InvalidOperationException(
            $"未在视觉树中找到 {typeof(T).Name}。");
    }
}
