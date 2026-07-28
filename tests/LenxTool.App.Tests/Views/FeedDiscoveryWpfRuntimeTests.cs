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
using LenxTool.Core.Models;

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
                    Button preparePublish = FindDescendant<Button>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "准备加入共享目录");
                    CheckBox publishConfirmation =
                        FindDescendant<CheckBox>(
                            view,
                            element => AutomationProperties.GetName(element)
                                == "确认发布设置");
                    ComboBox publishCategory = FindDescendant<ComboBox>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "发布分类");
                    Button publish = FindDescendant<Button>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "确认加入共享目录");
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
                    Assert.Equal(
                        AutomationControlType.Button,
                        CreatePeer(preparePublish)
                            .GetAutomationControlType());
                    Assert.Equal(
                        AutomationControlType.CheckBox,
                        CreatePeer(publishConfirmation)
                            .GetAutomationControlType());
                    Assert.Equal(
                        AutomationControlType.ComboBox,
                        CreatePeer(publishCategory)
                            .GetAutomationControlType());
                    Assert.Equal(
                        AutomationControlType.Button,
                        CreatePeer(publish).GetAutomationControlType());
                    Assert.Single(candidates.Items);
                    Assert.True(input.Focus());
                    Assert.True(input.ActualHeight >= 36);
                    Assert.True(submit.ActualHeight >= 36);
                    Assert.True(preparePublish.Focus());
                    Assert.True(publishConfirmation.Focus());
                    Assert.True(publishCategory.Focus());
                    Assert.True(publish.IsEnabled);

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
        var preparePublish =
            new RelayCommand<FeedDiscoveryCandidateViewModel>(_ => { });
        var publish = new RelayCommand(() => { });
        var cancelPublish = new RelayCommand(() => { });
        var refreshCatalog = new RelayCommand(() => { });
        var category = new FeedPublishCategoryChoice(null, "未分类");
        var viewChoice =
            new FeedPublishViewChoice(null, "自动识别（默认文章）");
        var fullText = new FeedPublishFullTextChoice(
            FeedFullTextPolicy.None,
            "不抓取全文");
        return new RuntimeDiscoveryModel
        {
            SearchCommand = search,
            CancelCommand = cancel,
            RetryCommand = retry,
            PreparePublishCommand = preparePublish,
            PublishCommand = publish,
            CancelPublishCommand = cancelPublish,
            RefreshCatalogCommand = refreshCatalog,
            PublishCategories = [category],
            SelectedPublishCategory = category,
            PublishViewChoices = [viewChoice],
            SelectedPublishView = viewChoice,
            PublishFullTextChoices = [fullText],
            SelectedPublishFullText = fullText,
            Candidates =
            {
                new(
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
            }
        };
    }

    /// <summary>
    /// 运行时夹具保留可写输入属性，以符合 TextBox 默认双向绑定契约。
    /// </summary>
    private sealed class RuntimeDiscoveryModel
    {
        public string Title { get; init; } = "发现订阅";
        public string Subtitle { get; init; } = "候选预览与发布确认";
        public string Input { get; set; } = "reader";
        public string QueryKindLabel { get; init; } = "关键词";
        public string Status { get; init; } = "已找到 1 个候选。";
        public bool IsBusy { get; init; }
        public bool HasCandidates { get; init; } = true;
        public bool ShowEmptyState { get; init; }
        public bool CanShowRetry { get; init; }
        public bool HasPublishSelection { get; init; } = true;
        public bool ShowPublishConfirmation { get; init; } = true;
        public bool CanEditPublishPolicy { get; init; } = true;
        public bool CanEditDiscoveryInput { get; init; } = true;
        public string PublishPanelTitle { get; init; } =
            "确认加入共享目录";
        public string PublishValidationText { get; init; } =
            "请核对全部策略。";
        public string PublishNormalizedUrl { get; init; } =
            "https://feeds.example/feed.xml";
        public long CatalogVersion { get; init; } = 7;
        public IReadOnlyList<int> PublishRefreshChoices { get; } =
            [15, 30, 60, 120];
        public int SelectedPublishRefreshMinutes { get; set; } = 60;
        public IReadOnlyList<FeedPublishCategoryChoice>
            PublishCategories { get; init; } = [];
        public FeedPublishCategoryChoice?
            SelectedPublishCategory { get; set; }
        public IReadOnlyList<FeedPublishViewChoice>
            PublishViewChoices { get; init; } = [];
        public FeedPublishViewChoice? SelectedPublishView { get; set; }
        public IReadOnlyList<FeedPublishFullTextChoice>
            PublishFullTextChoices { get; init; } = [];
        public FeedPublishFullTextChoice?
            SelectedPublishFullText { get; set; }
        public bool IsPublishConfirmed { get; set; } = true;
        public RelayCommand SearchCommand { get; init; } = null!;
        public RelayCommand CancelCommand { get; init; } = null!;
        public RelayCommand RetryCommand { get; init; } = null!;
        public RelayCommand<FeedDiscoveryCandidateViewModel>
            PreparePublishCommand { get; init; } = null!;
        public RelayCommand PublishCommand { get; init; } = null!;
        public RelayCommand CancelPublishCommand { get; init; } = null!;
        public RelayCommand RefreshCatalogCommand { get; init; } = null!;
        public List<FeedDiscoveryCandidateViewModel> Candidates { get; } =
            [];
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
