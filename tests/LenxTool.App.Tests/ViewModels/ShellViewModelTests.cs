using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;

namespace LenxTool.App.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    [Fact]
    public void NavigateChangesCurrentPageAndClosesCommandPalette()
    {
        TestPageViewModel home = new("首页");
        TestPageViewModel news = new("资讯中心");
        ShellViewModel shell = new(
        [
            new("home", "首页", "今日概览", IconData, home),
            new("news", "资讯中心", "早报与热点", IconData, news)
        ], new FakeAccountSessionService());
        shell.IsCommandPaletteOpen = true;

        shell.NavigateCommand.Execute("news");

        Assert.Same(news, shell.CurrentPage);
        Assert.Equal("news", shell.SelectedPageId);
        Assert.False(shell.IsCommandPaletteOpen);
    }

    [Fact]
    public void CommandQueryFiltersByLabelAndDescription()
    {
        ShellViewModel shell = new(
        [
            new("home", "首页", "今日概览", IconData, new TestPageViewModel("首页")),
            new("media", "媒体工作台", "字幕与音频", IconData, new TestPageViewModel("媒体")),
            new("settings", "设置", "主题与密钥", IconData, new TestPageViewModel("设置"))
        ], new FakeAccountSessionService());

        shell.CommandQuery = "字幕";

        PageNavigationItem item = Assert.Single(shell.FilteredCommands);
        Assert.Equal("media", item.Id);
    }

    [Fact]
    public void NavigateNotifiesSharedSectionPageOfTheSelectedTopLevelRoute()
    {
        TestSectionPageViewModel news = new();
        ShellViewModel shell = new(
        [
            new("home", "首页", "今日概览", IconData, new TestPageViewModel("首页")),
            new("news", "资讯列表", "浏览订阅内容", IconData, news),
            new("daily-briefing", "每日早报", "查看每日概览", IconData, news),
            new("trends", "热点趋势", "查看平台热榜", IconData, news),
            new("ai-reports", "AI 报告", "查看本地报告", IconData, news)
        ], new FakeAccountSessionService());

        shell.NavigateCommand.Execute("daily-briefing");

        Assert.Same(news, shell.CurrentPage);
        Assert.Equal("daily-briefing", shell.SelectedPageId);
        Assert.Equal("daily-briefing", news.LastRouteId);
    }

    [Fact]
    public void AdminNavigationTracksServerSessionRoleAndProtectsCurrentPage()
    {
        var account = new FakeAccountSessionService();
        TestPageViewModel home = new("首页");
        TestPageViewModel news = new("资讯中心");
        TestPageViewModel admin = new("订阅管理");
        ShellViewModel shell = new(
        [
            new("home", "首页", "今日概览", IconData, home),
            new("news", "资讯中心", "早报与热点", IconData, news),
            new("feed-admin", "订阅管理", "管理员共享目录", IconData, admin, AdminOnly: true)
        ], account);

        Assert.DoesNotContain(shell.NavigationItems, item => item.Id == "feed-admin");
        Assert.Contains(shell.NavigationItems, item => item.Id == "news");

        account.SetSession(SignedIn(AccountRole.Admin));
        Assert.Contains(shell.NavigationItems, item => item.Id == "feed-admin");
        shell.NavigateCommand.Execute("feed-admin");
        Assert.Same(admin, shell.CurrentPage);

        account.SetSession(SignedIn(AccountRole.User));
        Assert.DoesNotContain(shell.NavigationItems, item => item.Id == "feed-admin");
        Assert.Same(home, shell.CurrentPage);
        Assert.Equal("owner · 普通用户", shell.CloudAccountStatus);
    }

    [Fact]
    public async Task EntityNavigationRejectsUnknownRouteBeforeCurrentPageDispatch()
    {
        var home = new EntityPageViewModel("首页");
        var news = new EntityPageViewModel("资讯中心");
        ShellViewModel shell = new(
        [
            new("home", "首页", "今日概览", IconData, home),
            new("news", "资讯中心", "订阅内容", IconData, news)
        ], new FakeAccountSessionService());

        await shell.NavigateAsync(
            new("https://example.com", "feed_entry", "entry-1"),
            CancellationToken.None);

        Assert.Same(home, shell.CurrentPage);
        Assert.Empty(home.OpenedEntities);
        Assert.Empty(news.OpenedEntities);
    }

    [Fact]
    public async Task EntityNavigationDispatchesOnlyToTheSelectedTargetPage()
    {
        var home = new EntityPageViewModel("首页");
        var news = new EntityPageViewModel("资讯中心");
        ShellViewModel shell = new(
        [
            new("home", "首页", "今日概览", IconData, home),
            new("news", "资讯中心", "订阅内容", IconData, news)
        ], new FakeAccountSessionService());

        await shell.NavigateAsync(
            new("news", "feed_entry", "entry-1"),
            CancellationToken.None);

        Assert.Same(news, shell.CurrentPage);
        Assert.Empty(home.OpenedEntities);
        Assert.Equal(
            [("feed_entry", "entry-1")],
            news.OpenedEntities);
    }

    private const string IconData = "M0,0 L1,0 1,1 0,1 Z";

    private static AccountSessionSnapshot SignedIn(AccountRole role) => new(
        AccountSessionStatus.SignedIn,
        new("10000000-0000-4000-8000-000000000001", "owner", role),
        new(new DateOnly(2026, 7, 22), new(100, 0, 0, 100), new(3600, 0, 0, 3600)));

    private sealed class FakeAccountSessionService : IAccountSessionService
    {
        public bool IsConfigured => true;
        public AccountSessionSnapshot Current { get; private set; } = AccountSessionSnapshot.SignedOut;
        public event EventHandler<AccountSessionChangedEventArgs>? SessionChanged;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoginAsync(string username, string password, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void SetSession(AccountSessionSnapshot session)
        {
            Current = session;
            SessionChanged?.Invoke(this, new(session));
        }
    }

    private sealed class TestPageViewModel(string title) : PageViewModel(title, string.Empty);

    private sealed class TestSectionPageViewModel
        : PageViewModel, INavigationAware
    {
        public TestSectionPageViewModel()
            : base("资讯列表", string.Empty)
        {
        }

        public string? LastRouteId { get; private set; }

        public void OnNavigated(string routeId) => LastRouteId = routeId;
    }

    private sealed class EntityPageViewModel(string title)
        : PageViewModel(title, string.Empty), IEntityNavigationAware
    {
        public List<(string EntityType, string EntityId)> OpenedEntities
        { get; } = [];

        public Task OpenEntityAsync(
            string entityType,
            string entityId,
            CancellationToken cancellationToken)
        {
            OpenedEntities.Add((entityType, entityId));
            return Task.CompletedTask;
        }
    }
}
