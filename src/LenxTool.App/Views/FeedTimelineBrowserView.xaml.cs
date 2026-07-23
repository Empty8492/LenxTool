using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LenxTool.App.ViewModels;

namespace LenxTool.App.Views;

public partial class FeedTimelineBrowserView : UserControl
{
    private NewsCenterViewModel? _viewModel;
    private bool _restoringProgress;
    private bool _progressRestorePending;

    public FeedTimelineBrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        AttachViewModel(e.NewValue as NewsCenterViewModel);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as NewsCenterViewModel);
        ScheduleProgressRestore();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        DetachViewModel();

    private void AttachViewModel(NewsCenterViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NewsCenterViewModel.SelectedTimelineEntry)
            or nameof(NewsCenterViewModel.SelectedFeedArticle))
        {
            ScheduleProgressRestore();
        }
    }

    private void ScheduleProgressRestore()
    {
        _progressRestorePending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(RestoreProgress));
    }

    private void RestoreProgress()
    {
        if (_viewModel?.SelectedTimelineEntry is not { } item) return;
        double maximumOffset = ArticleScrollViewer.ExtentHeight - ArticleScrollViewer.ViewportHeight;
        if (maximumOffset <= 1) return;

        _progressRestorePending = false;
        _restoringProgress = true;
        try
        {
            ArticleScrollViewer.ScrollToVerticalOffset(
                maximumOffset * Math.Clamp(item.Progress, 0, 100) / 100d);
        }
        finally
        {
            _restoringProgress = false;
        }
    }

    private void ArticleScrollViewer_OnScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (_restoringProgress) return;
        if (_progressRestorePending)
        {
            RestoreProgress();
            if (_progressRestorePending) return;
        }
        if (_viewModel?.SelectedTimelineEntry is not { } item) return;
        double maximumOffset = e.ExtentHeight - e.ViewportHeight;
        double progress = maximumOffset <= 1
            ? 0
            : e.VerticalOffset / maximumOffset * 100;
        _viewModel.QueueTimelineProgress(item, progress);
    }

    private void ResetTimelineProgressClick(object sender, RoutedEventArgs e) =>
        ArticleScrollViewer.ScrollToTop();
}
