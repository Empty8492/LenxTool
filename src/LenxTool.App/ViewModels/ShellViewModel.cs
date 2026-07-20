using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;

namespace LenxTool.App.ViewModels;

public abstract class PageViewModel(string title, string subtitle) : ObservableObject
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
}

public sealed record PageNavigationItem(
    string Id,
    string Label,
    string Description,
    string IconData,
    PageViewModel ViewModel);

public sealed class ShellViewModel : ObservableObject
{
    private PageViewModel _currentPage;
    private string _selectedPageId;
    private bool _isCommandPaletteOpen;
    private string _commandQuery = string.Empty;

    public ShellViewModel(IEnumerable<PageNavigationItem> pages)
    {
        PageNavigationItem[] pageArray = pages.ToArray();
        if (pageArray.Length == 0) throw new ArgumentException("至少需要一个导航页面。", nameof(pages));

        NavigationItems = new(pageArray);
        _currentPage = pageArray[0].ViewModel;
        _selectedPageId = pageArray[0].Id;
        NavigateCommand = new RelayCommand<string>(Navigate);
        OpenCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = true);
        CloseCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = false);
    }

    public ObservableCollection<PageNavigationItem> NavigationItems { get; }
    public RelayCommand<string> NavigateCommand { get; }
    public RelayCommand OpenCommandPaletteCommand { get; }
    public RelayCommand CloseCommandPaletteCommand { get; }

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string SelectedPageId
    {
        get => _selectedPageId;
        set
        {
            if (string.Equals(_selectedPageId, value, StringComparison.Ordinal)) return;
            Navigate(value);
        }
    }

    public bool IsCommandPaletteOpen
    {
        get => _isCommandPaletteOpen;
        set => SetProperty(ref _isCommandPaletteOpen, value);
    }

    public string CommandQuery
    {
        get => _commandQuery;
        set
        {
            if (SetProperty(ref _commandQuery, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(FilteredCommands));
            }
        }
    }

    public IReadOnlyList<PageNavigationItem> FilteredCommands
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CommandQuery)) return NavigationItems;
            return NavigationItems
                .Where(item => item.Label.Contains(CommandQuery, StringComparison.CurrentCultureIgnoreCase) ||
                               item.Description.Contains(CommandQuery, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        }
    }

    private void Navigate(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return;
        PageNavigationItem? target = NavigationItems.FirstOrDefault(
            item => string.Equals(item.Id, pageId, StringComparison.Ordinal));
        if (target is null) return;

        CurrentPage = target.ViewModel;
        SetProperty(ref _selectedPageId, target.Id, nameof(SelectedPageId));
        IsCommandPaletteOpen = false;
        CommandQuery = string.Empty;
    }
}
