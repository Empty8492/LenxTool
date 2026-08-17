using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.App.Views;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;

namespace LenxTool.App.Tests.Views;

[Collection(WpfRuntimeGroup.Name)]
public sealed class ToolsWpfRuntimeTests
{
    [Fact]
    public void JsonDiffRunsWithNativeKeyboardControlsInRealMinimumShell()
    {
        Exception? failure = null;
        string stage = "starting";
        WpfRuntimeHost.Run(
            () =>
            {
                Window? window = null;
                var themeService = new ThemeService();
                try
                {
                    themeService.ApplyTheme(useDarkTheme: false);
                    var viewModel = new ToolsViewModel(
                        new StubFileHashService(),
                        new StubDocumentConverter(),
                        new StubDialogs());
                    var shell = new ShellViewModel(
                        [
                            new(
                                "tools",
                                "文档与数据",
                                "文档转换与 JSON 工具",
                                "M0,0 L1,0 1,1 0,1 Z",
                                viewModel)
                        ],
                        new StubAccountSession());
                    window = new MainWindow(shell)
                    {
                        Width = 920,
                        Height = 620,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false
                    };
                    window.Show();
                    window.UpdateLayout();
                    ToolsView view = FindDescendant<ToolsView>(
                        window,
                        _ => true);

                    stage = "opening JSON diff tab";
                    TabControl tabs = FindDescendant<TabControl>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "文档与数据工具模式");
                    tabs.SelectedIndex = 1;
                    PumpDispatcher();
                    window.UpdateLayout();

                    ScrollViewer diffScrollViewer =
                        FindDescendant<ScrollViewer>(
                            view,
                            element => AutomationProperties.GetName(element)
                                == "JSON Diff 内容滚动区");

                    TextBox left = FindDescendant<TextBox>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "左侧 JSON");
                    TextBox right = FindDescendant<TextBox>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "右侧 JSON");
                    Button compare = FindDescendant<Button>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "比较 JSON 结构");
                    Button cancel = FindDescendant<Button>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "取消 JSON 比较");
                    Button swap = FindDescendant<Button>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "交换左右 JSON");
                    ListBox differences = FindDescendant<ListBox>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "JSON 结构差异列表");

                    stage = "checking native automation and focus";
                    Assert.Equal(
                        AutomationControlType.Edit,
                        CreatePeer(left).GetAutomationControlType());
                    Assert.Equal(
                        AutomationControlType.Edit,
                        CreatePeer(right).GetAutomationControlType());
                    Assert.Equal(
                        AutomationControlType.Button,
                        CreatePeer(compare).GetAutomationControlType());
                    Assert.Equal(
                        AutomationControlType.Button,
                        CreatePeer(cancel).GetAutomationControlType());
                    Assert.Equal(
                        AutomationControlType.Button,
                        CreatePeer(swap).GetAutomationControlType());
                    Assert.False(left.AcceptsTab);
                    Assert.False(right.AcceptsTab);
                    Assert.True(left.Focus());
                    Assert.True(right.Focus());
                    Assert.True(compare.Focus());
                    Assert.True(swap.Focus());
                    Assert.True(diffScrollViewer.ScrollableHeight > 0);
                    Assert.Equal(0, diffScrollViewer.ScrollableWidth);

                    stage = "running bound comparison";
                    viewModel.LeftJson = "{\"changed\":1,\"removed\":2}";
                    viewModel.RightJson = "{\"added\":3,\"changed\":4}";
                    Assert.Same(viewModel.CompareJsonCommand, compare.Command);
                    compare.Command.Execute(compare.CommandParameter);
                    PumpUntil(
                        () => !viewModel.CompareJsonCommand.IsRunning,
                        TimeSpan.FromSeconds(5));
                    window.UpdateLayout();

                    Assert.Equal(3, viewModel.Differences.Count);
                    Assert.Equal(3, differences.Items.Count);
                    differences.BringIntoView();
                    PumpDispatcher();
                    Assert.True(IsWithinViewport(
                        differences,
                        diffScrollViewer));

                    stage = "checking production list virtualization";
                    viewModel.LeftJson = "{" + string.Join(
                        ',',
                        Enumerable.Range(0, 500)
                            .Select(index => $"\"p{index:D3}\":0")) + "}";
                    viewModel.RightJson = "{" + string.Join(
                        ',',
                        Enumerable.Range(0, 500)
                            .Select(index => $"\"p{index:D3}\":1")) + "}";
                    compare.Command.Execute(compare.CommandParameter);
                    PumpUntil(
                        () => !viewModel.CompareJsonCommand.IsRunning,
                        TimeSpan.FromSeconds(5));
                    window.UpdateLayout();
                    differences.ScrollIntoView(differences.Items[0]);
                    PumpDispatcher();
                    Assert.Equal(500, differences.Items.Count);
                    Assert.NotNull(
                        differences.ItemContainerGenerator
                            .ContainerFromIndex(0));
                    Assert.Null(
                        differences.ItemContainerGenerator
                            .ContainerFromIndex(499));

                    stage = "checking 200 percent layout";
                    window.Width = 1520;
                    window.Height = 1240;
                    view.LayoutTransform = new ScaleTransform(2d, 2d);
                    window.UpdateLayout();
                    PumpDispatcher();
                    Assert.True(diffScrollViewer.ScrollableWidth > 0);
                    right.BringIntoView();
                    PumpDispatcher();
                    Assert.True(IsWithinViewport(right, diffScrollViewer));
                    differences.BringIntoView();
                    PumpDispatcher();
                    Assert.True(IsWithinViewport(
                        differences,
                        diffScrollViewer));

                    stage = "switching semantic theme";
                    string lightBackground = left.Background.ToString(
                        CultureInfo.InvariantCulture);
                    themeService.ApplyTheme(useDarkTheme: true);
                    PumpDispatcher();
                    string darkBackground = left.Background.ToString(
                        CultureInfo.InvariantCulture);
                    Assert.NotEqual(lightBackground, darkBackground);
                    stage = "completed assertions";
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
            () => $"JSON Diff WPF 运行时验收超时，阶段：{stage}。");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static AutomationPeer CreatePeer(UIElement element) =>
        UIElementAutomationPeer.CreatePeerForElement(element)
        ?? throw new InvalidOperationException(
            $"{element.GetType().Name} 没有创建自动化 Peer。");

    private static bool IsWithinViewport(
        FrameworkElement element,
        ScrollViewer scrollViewer)
    {
        Rect bounds = element.TransformToAncestor(scrollViewer)
            .TransformBounds(new(new Point(), element.RenderSize));
        return bounds.Bottom > 0
            && bounds.Top < scrollViewer.ViewportHeight
            && bounds.Right > 0
            && bounds.Left < scrollViewer.ViewportWidth;
    }

    private static T FindDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (int index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
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

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("等待 JSON Diff 操作完成超时。");
            }

            PumpDispatcher();
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class StubFileHashService : IFileHashService
    {
        public Task<string> ComputeSha256Async(
            string filePath,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);
    }

    private sealed class StubDocumentConverter : IDocumentConverter
    {
        public string Name => "Stub";
        public bool IsAvailable => false;

        public Task ConvertToPdfAsync(
            string sourcePath,
            string destinationPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubDialogs : IDesktopFileDialogService
    {
        public IReadOnlyList<string> PickMediaFiles() => [];
        public string? PickWhisperModel() => null;
        public string? PickDatabaseBackup() => null;
        public string? PickFileForHash() => null;
        public (string Source, string Destination)? PickWordConversion() => null;
        public string? PickFolder() => null;
        public void OpenFolder(string path) { }
        public void OpenUri(string uri) { }
    }

    private sealed class StubAccountSession : IAccountSessionService
    {
        public bool IsConfigured => false;
        public AccountSessionSnapshot Current =>
            AccountSessionSnapshot.SignedOut;
        public event EventHandler<AccountSessionChangedEventArgs>?
            SessionChanged
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task LoginAsync(
            string username,
            string password,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RefreshAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task LogoutAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
