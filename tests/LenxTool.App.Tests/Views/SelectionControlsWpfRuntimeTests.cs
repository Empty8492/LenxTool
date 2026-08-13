using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LenxTool.App.Services;
using LenxTool.App.Views;

namespace LenxTool.App.Tests.Views;

[Collection(WpfRuntimeGroup.Name)]
public sealed class SelectionControlsWpfRuntimeTests
{
    [Fact]
    public void FeedSelectionControlsKeepNativeBehaviorAcrossThemesAndLayoutScales()
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
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                    themeService.ApplyTheme(useDarkTheme: false);

                    stage = "creating narrow feed view";
                    var view = new FeedTimelineView();
                    var scrollViewer = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = view
                    };
                    window = new Window
                    {
                        Title = "Selection controls runtime acceptance",
                        Width = 900,
                        Height = 620,
                        Left = -10000,
                        Top = -10000,
                        ShowInTaskbar = false,
                        Content = scrollViewer
                    };
                    window.Show();
                    window.Activate();
                    PumpDispatcher();

                    TabControl tabs = FindDescendant<TabControl>(
                        view,
                        element => AutomationProperties.GetName(element) == "Feed 视图切换");
                    tabs.SelectedIndex = 0;
                    PumpDispatcher();
                    DatePicker datePicker = FindDescendant<DatePicker>(
                        view,
                        element => AutomationProperties.GetName(element) == "Feed 日期筛选");
                    CheckBox favorites = FindDescendant<CheckBox>(
                        view,
                        element => AutomationProperties.GetName(element) == "仅看 Feed 收藏");
                    ComboBox category = FindDescendant<ComboBox>(
                        view,
                        element => AutomationProperties.GetName(element) == "Feed 分类筛选");

                    stage = "checking native automation peers";
                    Assert.Equal(
                        AutomationControlType.Tab,
                        CreatePeer(tabs).GetAutomationControlType());
                    AutomationPeer datePickerPeer = CreatePeer(datePicker);
                    Assert.Equal("DatePicker", datePickerPeer.GetClassName());
                    Assert.Equal("Feed 日期筛选", datePickerPeer.GetName());
                    Assert.Equal(
                        AutomationControlType.CheckBox,
                        CreatePeer(favorites).GetAutomationControlType());
                    Assert.Equal(
                        AutomationControlType.ComboBox,
                        CreatePeer(category).GetAutomationControlType());

                    stage = "checking keyboard tab navigation";
                    var firstTab = Assert.IsType<TabItem>(tabs.Items[0]);
                    Assert.True(firstTab.Focus());
                    Keyboard.Focus(firstTab);
                    PumpDispatcher();
                    firstTab.RaiseEvent(new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(firstTab),
                        Environment.TickCount,
                        Key.Right)
                    {
                        RoutedEvent = Keyboard.KeyDownEvent
                    });
                    PumpDispatcher();
                    Assert.Equal(1, tabs.SelectedIndex);

                    stage = "opening native date picker";
                    tabs.SelectedIndex = 0;
                    datePicker.IsDropDownOpen = true;
                    PumpUntil(
                        () => datePicker.IsDropDownOpen
                              && datePicker.Template.FindName("PART_Popup", datePicker)
                                  is System.Windows.Controls.Primitives.Popup
                              {
                                  Child: System.Windows.Controls.Calendar
                              },
                        TimeSpan.FromSeconds(5));
                    var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(
                        datePicker.Template.FindName("PART_Popup", datePicker));
                    var calendar = Assert.IsType<System.Windows.Controls.Calendar>(
                        popup.Child);
                    var dateTextBox =
                        Assert.IsType<System.Windows.Controls.Primitives.DatePickerTextBox>(
                            datePicker.Template.FindName("PART_TextBox", datePicker));
                    var watermark = Assert.IsType<ContentControl>(
                        dateTextBox.Template.FindName("PART_Watermark", dateTextBox));
                    Assert.Equal(Visibility.Visible, watermark.Visibility);
                    Assert.NotNull(watermark.Content);
                    System.Windows.Controls.Primitives.CalendarItem? calendarItem = null;
                    Button? previousButton = null;
                    Button? headerButton = null;
                    Button? nextButton = null;
                    // 让 Popup 内的嵌套模板沿真实 Loaded/布局流程自然完成，再查询
                    // Automation 树；测试不主动把 Calendar 推进到半初始化的中间态。
                    PumpUntil(
                        () =>
                        {
                            calendarItem = calendar.Template.FindName(
                                "PART_CalendarItem",
                                calendar) as System.Windows.Controls.Primitives.CalendarItem;
                            if (calendarItem is not { IsLoaded: true })
                            {
                                return false;
                            }
                            previousButton = calendarItem.Template.FindName(
                                "PART_PreviousButton",
                                calendarItem) as Button;
                            headerButton = calendarItem.Template.FindName(
                                "PART_HeaderButton",
                                calendarItem) as Button;
                            nextButton = calendarItem.Template.FindName(
                                "PART_NextButton",
                                calendarItem) as Button;
                            return previousButton is { IsLoaded: true }
                                   && headerButton is { IsLoaded: true }
                                   && nextButton is { IsLoaded: true };
                        },
                        TimeSpan.FromSeconds(5));
                    var readyHeaderButton = Assert.IsType<Button>(headerButton);
                    var readyNextButton = Assert.IsType<Button>(nextButton);
                    List<AutomationPeer>? calendarChildren = CreatePeer(calendar).GetChildren();
                    Assert.NotNull(calendarChildren);
                    Assert.Contains(
                        calendarChildren,
                        peer => peer.GetName() == "上一个月");
                    Assert.Contains(
                        calendarChildren,
                        peer => peer.GetName() == "切换月份和年份");
                    Assert.Contains(
                        calendarChildren,
                        peer => peer.GetName() == "下一个月");
                    Assert.Contains(
                        calendarChildren,
                        peer => peer is DateTimeAutomationPeer);
                    readyHeaderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpDispatcher();
                    Assert.Equal(CalendarMode.Year, calendar.DisplayMode);
                    readyHeaderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpDispatcher();
                    Assert.Equal(CalendarMode.Decade, calendar.DisplayMode);
                    calendar.DisplayMode = CalendarMode.Month;
                    PumpDispatcher();
                    DateTime displayedMonth = calendar.DisplayDate;
                    readyNextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpDispatcher();
                    Assert.Equal(
                        displayedMonth.AddMonths(1).Month,
                        calendar.DisplayDate.Month);
                    DateTime selectedDate = DateTime.Today.AddDays(2);
                    calendar.SelectedDate = selectedDate;
                    PumpDispatcher();
                    Assert.Equal(selectedDate, datePicker.SelectedDate);
                    Assert.False(string.IsNullOrWhiteSpace(datePicker.Text));
                    Assert.False(string.IsNullOrWhiteSpace(dateTextBox.Text));
                    Assert.Equal(Visibility.Collapsed, watermark.Visibility);
                    Assert.Same(
                        Application.Current.FindResource("CompactCalendarStyle"),
                        calendar.Style);
                    datePicker.IsDropDownOpen = false;

                    stage = "typing a date with the native text box";
                    datePicker.SelectedDate = null;
                    PumpDispatcher();
                    Assert.True(dateTextBox.Focus());
                    Keyboard.Focus(dateTextBox);
                    DateTime typedDate = selectedDate.AddDays(1);
                    dateTextBox.Text = typedDate.ToString(
                        "d",
                        CultureInfo.CurrentCulture);
                    Assert.True(category.Focus());
                    Keyboard.Focus(category);
                    PumpDispatcher();
                    Assert.Equal(typedDate, datePicker.SelectedDate);

                    stage = "checking narrow and 200 percent layouts";
                    tabs.SelectedIndex = 1;
                    PumpDispatcher();
                    Assert.Equal(0, scrollViewer.ScrollableWidth);
                    Assert.InRange(tabs.ActualWidth, 1, scrollViewer.ViewportWidth);

                    window.Width = 1800;
                    window.Height = 1240;
                    view.LayoutTransform = new ScaleTransform(2d, 2d);
                    window.UpdateLayout();
                    PumpDispatcher();
                    Assert.Equal(0, scrollViewer.ScrollableWidth);

                    stage = "switching semantic themes";
                    string lightBackground = datePicker.Background.ToString(
                        CultureInfo.InvariantCulture);
                    themeService.ApplyTheme(useDarkTheme: true);
                    PumpDispatcher();
                    string darkBackground = datePicker.Background.ToString(
                        CultureInfo.InvariantCulture);
                    Assert.NotEqual(lightBackground, darkBackground);
                    Assert.True(datePicker.ActualHeight >= 36);
                    Assert.True(favorites.ActualHeight >= 36);
                    Assert.True(category.ActualHeight >= 36);
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
                    SynchronizationContext.SetSynchronizationContext(null);
                }
            },
            TimeSpan.FromSeconds(20),
            () => $"Selection-controls runtime acceptance timed out at stage: {stage}.");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static AutomationPeer CreatePeer(UIElement element) =>
        UIElementAutomationPeer.CreatePeerForElement(element)
        ?? throw new InvalidOperationException(
            $"{element.GetType().Name} did not create an automation peer.");

    private static T FindDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }
            try
            {
                return FindDescendant(child, predicate);
            }
            catch (InvalidOperationException)
            {
            }
        }
        throw new InvalidOperationException(
            $"Could not find {typeof(T).Name} in the visual tree.");
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException(
                    "Timed out while pumping the WPF dispatcher.");
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
}
