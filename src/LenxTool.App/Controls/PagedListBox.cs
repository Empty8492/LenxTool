using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LenxTool.App.Controls;

public sealed class PagedListBox : ListBox
{
    // 像素滚动下提前约一个卡片区加载，避免到达底部后才开始等待下一页。
    internal const double DefaultLoadMoreThreshold = 240d;

    public static readonly DependencyProperty LoadMoreCommandProperty = DependencyProperty.Register(
        nameof(LoadMoreCommand),
        typeof(ICommand),
        typeof(PagedListBox),
        new PropertyMetadata(null));

    public static readonly DependencyProperty LoadMoreThresholdProperty = DependencyProperty.Register(
        nameof(LoadMoreThreshold),
        typeof(double),
        typeof(PagedListBox),
        new PropertyMetadata(DefaultLoadMoreThreshold));

    private ScrollViewer? _scrollViewer;

    public ICommand? LoadMoreCommand
    {
        get => (ICommand?)GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    public double LoadMoreThreshold
    {
        get => (double)GetValue(LoadMoreThresholdProperty);
        set => SetValue(LoadMoreThresholdProperty, value);
    }

    public override void OnApplyTemplate()
    {
        DetachScrollViewer();
        base.OnApplyTemplate();
        _scrollViewer = GetTemplateChild("PART_ScrollViewer") as ScrollViewer
            ?? FindScrollViewer(this);
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (VisualParent is null)
        {
            DetachScrollViewer();
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs eventArgs)
    {
        if ((eventArgs.ExtentHeightChange == 0
             && eventArgs.VerticalChange == 0)
            || _scrollViewer is null
            || _scrollViewer.ScrollableHeight <= 0
            || _scrollViewer.VerticalOffset
                < _scrollViewer.ScrollableHeight - Math.Max(0, LoadMoreThreshold))
        {
            return;
        }

        ICommand? command = LoadMoreCommand;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private void DetachScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scrollViewer) return scrollViewer;
            ScrollViewer? descendant = FindScrollViewer(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }
}
