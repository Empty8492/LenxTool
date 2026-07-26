using System.Collections.ObjectModel;
using System.Threading;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;

namespace LenxTool.App.ViewModels;

public abstract class PageViewModel(string title, string subtitle) : ObservableObject
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
}

public interface INavigationAware
{
    void OnNavigated(string routeId);
}

public sealed record PageNavigationItem(
    string Id,
    string Label,
    string Description,
    string IconData,
    PageViewModel ViewModel,
    bool AdminOnly = false);

public sealed class ShellViewModel : ObservableObject
{
    private PageViewModel _currentPage;
    private string _selectedPageId;
    private bool _isCommandPaletteOpen;
    private string _commandQuery = string.Empty;
    private readonly PageNavigationItem[] _allPages;
    private readonly PageNavigationItem _fallbackPage;
    private readonly IAccountSessionService _accountSession;
    private readonly SynchronizationContext? _synchronizationContext;
    private string _cloudAccountStatus = "云服务未登录 · 可离线使用";

    public ShellViewModel(
        IEnumerable<PageNavigationItem> pages,
        IAccountSessionService accountSession,
        NotificationCenterViewModel? notificationCenter = null)
    {
        PageNavigationItem[] pageArray = pages.ToArray();
        if (pageArray.Length == 0) throw new ArgumentException("至少需要一个导航页面。", nameof(pages));
        _fallbackPage = pageArray.FirstOrDefault(page => !page.AdminOnly)
            ?? throw new ArgumentException("至少需要一个普通用户可见页面。", nameof(pages));
        _allPages = pageArray;
        _accountSession = accountSession;
        _synchronizationContext = SynchronizationContext.Current;
        NotificationCenter = notificationCenter;

        NavigationItems = [];
        _currentPage = _fallbackPage.ViewModel;
        _selectedPageId = _fallbackPage.Id;
        NavigateCommand = new RelayCommand<string>(Navigate);
        OpenCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = true);
        CloseCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = false);
        _accountSession.SessionChanged += OnAccountSessionChanged;
        ApplyAccountSession(_accountSession.Current);
    }

    public ObservableCollection<PageNavigationItem> NavigationItems { get; }
    public NotificationCenterViewModel? NotificationCenter { get; }
    public RelayCommand<string> NavigateCommand { get; }
    public RelayCommand OpenCommandPaletteCommand { get; }
    public RelayCommand CloseCommandPaletteCommand { get; }
    public string CloudAccountStatus
    {
        get => _cloudAccountStatus;
        private set => SetProperty(ref _cloudAccountStatus, value);
    }

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
        if (target.ViewModel is INavigationAware navigationAware)
        {
            navigationAware.OnNavigated(target.Id);
        }
        SetProperty(ref _selectedPageId, target.Id, nameof(SelectedPageId));
        IsCommandPaletteOpen = false;
        CommandQuery = string.Empty;
    }

    internal async Task NavigateAsync(
        AppNavigationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Navigate(request.RouteId);
        if (CurrentPage is IEntityNavigationAware target)
        {
            await target.OpenEntityAsync(
                    request.EntityType,
                    request.EntityId,
                    cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private void OnAccountSessionChanged(object? sender, AccountSessionChangedEventArgs eventArgs)
    {
        if (_synchronizationContext is not null && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(_ => ApplyAccountSession(eventArgs.Session), null);
            return;
        }
        ApplyAccountSession(eventArgs.Session);
    }

    private void ApplyAccountSession(AccountSessionSnapshot session)
    {
        PageNavigationItem[] visible = _allPages
            .Where(page => !page.AdminOnly || session.IsAdmin)
            .ToArray();
        NavigationItems.Clear();
        foreach (PageNavigationItem page in visible) NavigationItems.Add(page);

        if (visible.All(page => !ReferenceEquals(page.ViewModel, CurrentPage)))
        {
            CurrentPage = _fallbackPage.ViewModel;
            SetProperty(ref _selectedPageId, _fallbackPage.Id, nameof(SelectedPageId));
        }
        OnPropertyChanged(nameof(FilteredCommands));
        CloudAccountStatus = session.Status switch
        {
            AccountSessionStatus.SignedIn =>
                $"{session.User!.Username} · {(session.IsAdmin ? "管理员" : "普通用户")}",
            AccountSessionStatus.Expired => "云服务会话已过期 · 请重新登录",
            _ when !_accountSession.IsConfigured => "云服务未配置 · 可离线使用",
            _ => "云服务未登录 · 可离线使用"
        };
    }
}
