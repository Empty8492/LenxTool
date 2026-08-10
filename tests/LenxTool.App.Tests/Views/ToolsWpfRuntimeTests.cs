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
using LenxTool.Core.Contracts;

namespace LenxTool.App.Tests.Views;

[Collection(WpfRuntimeGroup.Name)]
public sealed class ToolsWpfRuntimeTests
{
    [Fact]
    public void JsonDiffRunsWithNativeKeyboardControlsAtNarrowLayout()
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
                    var view = new ToolsView { DataContext = viewModel };
                    var scrollViewer = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = view
                    };
                    window = new Window
                    {
                        Width = 760,
                        Height = 620,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = scrollViewer
                    };
                    window.Show();
                    window.UpdateLayout();

                    stage = "opening JSON diff tab";
                    TabControl tabs = FindDescendant<TabControl>(
                        view,
                        element => AutomationProperties.GetName(element)
                            == "文档与数据工具模式");
                    tabs.SelectedIndex = 1;
                    PumpDispatcher();

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
                    Assert.Equal(0, scrollViewer.ScrollableWidth);

                    stage = "checking 200 percent layout";
                    window.Width = 1520;
                    window.Height = 1240;
                    view.LayoutTransform = new ScaleTransform(2d, 2d);
                    window.UpdateLayout();
                    PumpDispatcher();
                    Assert.Equal(0, scrollViewer.ScrollableWidth);

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
}
