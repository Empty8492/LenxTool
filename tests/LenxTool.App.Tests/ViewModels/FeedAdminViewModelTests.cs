using LenxTool.App.ViewModels;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class FeedAdminViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AdminInitializationLoadsAllCatalogWhileUserCommandsRemainUnavailable()
    {
        FeedCatalogSnapshot snapshot = Snapshot(7);
        var admin = CreateViewModel(snapshot, AccountRole.Admin);

        await admin.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(7, admin.ViewModel.CatalogVersion);
        Assert.Single(admin.ViewModel.Categories);
        Assert.Single(admin.ViewModel.Feeds);
        Assert.True(admin.ViewModel.RefreshCommand.CanExecute(null));

        var user = CreateViewModel(snapshot, AccountRole.User);
        await user.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.Empty(user.ViewModel.Categories);
        Assert.False(user.ViewModel.RefreshCommand.CanExecute(null));
        Assert.Contains("管理员", user.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryShowsTitleSiteTypeAndWarningBeforeEnablingSave()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.Discovery.Result = new(
            "https://site.example/blog/",
            [new("https://site.example/atom.xml", "站点更新", FeedDocumentKind.Atom)]);
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.BeginNewFeedCommand.Execute(null);
        context.ViewModel.FeedUrlInput = "https://site.example/blog/";

        await context.ViewModel.DiscoverCommand.ExecuteAsync();

        Assert.True(context.ViewModel.HasDiscoveryPreview);
        Assert.Equal("站点更新", context.ViewModel.DiscoveryTitle);
        Assert.Equal("https://site.example/blog/", context.ViewModel.DiscoverySite);
        Assert.Equal("Atom", context.ViewModel.DiscoveryType);
        Assert.Contains("安全", context.ViewModel.DiscoveryWarning, StringComparison.Ordinal);
        Assert.Equal("https://site.example/atom.xml", context.ViewModel.FeedUrlInput);
        Assert.Equal("站点更新", context.ViewModel.FeedDisplayNameInput);
        Assert.True(context.ViewModel.SaveFeedCommand.CanExecute(null));

        context.ViewModel.FeedUrlInput = "https://site.example/changed.xml";

        Assert.False(context.ViewModel.HasDiscoveryPreview);
        Assert.False(context.ViewModel.SaveFeedCommand.CanExecute(null));
    }

    [Fact]
    public async Task SavingVerifiedFeedUsesCurrentVersionThenSynchronizesLatestCatalog()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.Discovery.Result = new(
            "https://new.example/",
            [new("https://new.example/feed.xml", "新订阅", FeedDocumentKind.Rss20)]);
        context.Sync.OnSync = () =>
        {
            context.Repository.Snapshot = Snapshot(8) with
            {
                Feeds =
                [
                    .. Snapshot(8).Feeds,
                    Feed("10000000-0000-4000-8000-000000000099", "新订阅", 200)
                ]
            };
        };
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.BeginNewFeedCommand.Execute(null);
        context.ViewModel.FeedUrlInput = "https://new.example/";
        await context.ViewModel.DiscoverCommand.ExecuteAsync();

        await context.ViewModel.SaveFeedCommand.ExecuteAsync();

        FeedCall call = Assert.Single(context.Admin.FeedCalls);
        Assert.Equal("create", call.Operation);
        Assert.Equal(7, call.ExpectedVersion);
        Assert.Equal("https://new.example/feed.xml", call.Input!.OriginalUrl);
        Assert.Equal(8, context.ViewModel.CatalogVersion);
        Assert.Equal(2, context.ViewModel.Feeds.Count);
        Assert.Contains("已保存", context.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionConflictRefreshesButDoesNotReplayMutation()
    {
        var context = CreateViewModel(Snapshot(4), AccountRole.Admin);
        context.Admin.Failure = new AppException(new(
            AppErrorCode.Conflict,
            "数据版本冲突",
            "目录已变化",
            "刷新后重试",
            "Worker error code: CATALOG_VERSION_CONFLICT"));
        context.Sync.OnSync = () => context.Repository.Snapshot = Snapshot(5) with
        {
            Categories = [Category("10000000-0000-4000-8000-000000000010", "其他管理员的新名称", 100)]
        };
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.SelectedCategory = context.ViewModel.Categories[0];
        context.ViewModel.CategoryNameInput = "我的旧编辑";

        await context.ViewModel.SaveCategoryCommand.ExecuteAsync();

        Assert.Single(context.Admin.CategoryCalls);
        Assert.Equal(1, context.Sync.SyncCount);
        Assert.Equal(5, context.ViewModel.CatalogVersion);
        Assert.Equal("其他管理员的新名称", context.ViewModel.Categories[0].Name);
        Assert.Contains("其他管理员", context.ViewModel.Status, StringComparison.Ordinal);
        Assert.Contains("重试", context.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleMoveAndTwoStepDeleteUseVersionedFeedMutations()
    {
        FeedCatalogSnapshot initial = Snapshot(10) with
        {
            Feeds =
            [
                Feed("10000000-0000-4000-8000-000000000020", "第一源", 100),
                Feed("10000000-0000-4000-8000-000000000021", "第二源", 200)
            ]
        };
        var context = CreateViewModel(initial, AccountRole.Admin);
        context.Sync.OnSync = () =>
        {
            FeedCatalogSnapshot current = context.Repository.Snapshot!;
            context.Repository.Snapshot = current with
            {
                State = current.State with { Version = current.State.Version + 1 }
            };
        };
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        FeedCatalogItem first = context.ViewModel.Feeds[0];

        await context.ViewModel.ToggleFeedCommand.ExecuteAsync(first);
        await context.ViewModel.MoveFeedDownCommand.ExecuteAsync(first);
        context.ViewModel.PrepareDeleteFeedCommand.Execute(first);

        Assert.Equal(first.Id, context.ViewModel.PendingDeleteFeedId);
        Assert.True(context.ViewModel.ConfirmDeleteFeedCommand.CanExecute(null));

        await context.ViewModel.ConfirmDeleteFeedCommand.ExecuteAsync();

        Assert.Collection(
            context.Admin.FeedCalls,
            call =>
            {
                Assert.Equal("update", call.Operation);
                Assert.False(call.Input!.IsEnabled);
            },
            call =>
            {
                Assert.Equal("update", call.Operation);
                Assert.True(call.Input!.SortOrder > 200);
            },
            call => Assert.Equal("delete", call.Operation));
    }

    private static TestContext CreateViewModel(FeedCatalogSnapshot snapshot, AccountRole role)
    {
        var repository = new FakeCatalogRepository { Snapshot = snapshot };
        var sync = new FakeCatalogSyncService();
        var admin = new FakeCatalogAdminService();
        var discovery = new FakeDiscoveryService();
        var account = new FakeAccountSessionService(SignedIn(role));
        var viewModel = new FeedAdminViewModel(admin, repository, sync, discovery, account);
        return new(viewModel, admin, repository, sync, discovery);
    }

    private static FeedCatalogSnapshot Snapshot(long version) => new(
        new(version, FeedCatalogScope.All, Now, Now),
        [Category("10000000-0000-4000-8000-000000000010", "技术", 100)],
        [Feed("10000000-0000-4000-8000-000000000020", "示例源", 100)]);

    private static FeedCategory Category(string id, string name, int sortOrder) => new(
        id, name, name.ToLowerInvariant(), sortOrder, true, 1, Now, Now);

    private static FeedCatalogItem Feed(string id, string name, int sortOrder) => new(
        id,
        $"https://feeds.example/{id}.xml",
        $"https://feeds.example/{id}.xml",
        name,
        "https://feeds.example/",
        "10000000-0000-4000-8000-000000000010",
        FeedViewKind.Article,
        60,
        sortOrder,
        true,
        1,
        Now,
        Now);

    private static AccountSessionSnapshot SignedIn(AccountRole role) => new(
        AccountSessionStatus.SignedIn,
        new("10000000-0000-4000-8000-000000000001", "owner", role),
        new(new DateOnly(2026, 7, 23), new(100, 0, 0, 100), new(3600, 0, 0, 3600)));

    private sealed record TestContext(
        FeedAdminViewModel ViewModel,
        FakeCatalogAdminService Admin,
        FakeCatalogRepository Repository,
        FakeCatalogSyncService Sync,
        FakeDiscoveryService Discovery);

    private sealed record FeedCall(
        string Operation,
        string? Id,
        FeedCatalogItemInput? Input,
        long ExpectedVersion);

    private sealed record CategoryCall(
        string Operation,
        string? Id,
        FeedCategoryInput? Input,
        long ExpectedVersion);

    private sealed class FakeCatalogAdminService : IFeedCatalogAdminService
    {
        public List<FeedCall> FeedCalls { get; } = [];
        public List<CategoryCall> CategoryCalls { get; } = [];
        public AppException? Failure { get; set; }

        public Task<long> CreateCategoryAsync(FeedCategoryInput input, long expected, CancellationToken token) =>
            Category("create", null, input, expected);
        public Task<long> UpdateCategoryAsync(string id, FeedCategoryInput input, long expected, CancellationToken token) =>
            Category("update", id, input, expected);
        public Task<long> DeleteCategoryAsync(string id, long expected, CancellationToken token) =>
            Category("delete", id, null, expected);
        public Task<long> CreateFeedAsync(FeedCatalogItemInput input, long expected, CancellationToken token) =>
            Feed("create", null, input, expected);
        public Task<long> UpdateFeedAsync(string id, FeedCatalogItemInput input, long expected, CancellationToken token) =>
            Feed("update", id, input, expected);
        public Task<long> DeleteFeedAsync(string id, long expected, CancellationToken token) =>
            Feed("delete", id, null, expected);

        private Task<long> Category(string operation, string? id, FeedCategoryInput? input, long expected)
        {
            CategoryCalls.Add(new(operation, id, input, expected));
            return Complete(expected);
        }

        private Task<long> Feed(string operation, string? id, FeedCatalogItemInput? input, long expected)
        {
            FeedCalls.Add(new(operation, id, input, expected));
            return Complete(expected);
        }

        private Task<long> Complete(long expected) => Failure is null
            ? Task.FromResult(expected + 1)
            : Task.FromException<long>(Failure);
    }

    private sealed class FakeCatalogRepository : IFeedCatalogRepository
    {
        public FeedCatalogSnapshot? Snapshot { get; set; }
        public Task ReplaceAsync(FeedCatalogSnapshot snapshot, CancellationToken token) => throw new NotSupportedException();
        public Task<FeedCatalogSnapshot?> GetCatalogAsync(FeedCatalogScope scope, CancellationToken token) =>
            Task.FromResult(Snapshot);
        public Task MarkSynchronizedAsync(long expected, DateTimeOffset at, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<FeedCatalogState> GetStateAsync(CancellationToken token) =>
            Task.FromResult(Snapshot?.State ?? new(0, FeedCatalogScope.Active, null, null));
    }

    private sealed class FakeCatalogSyncService : IFeedCatalogSyncService
    {
        public int SyncCount { get; private set; }
        public Action? OnSync { get; set; }
        public FeedCatalogSyncStatus Current { get; private set; } = new(
            false, 0, FeedCatalogScope.All, null, false, 0, null, null);
        public event EventHandler<FeedCatalogSyncStatusChangedEventArgs>? StatusChanged;
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<FeedCatalogSyncResult> SyncAsync(CancellationToken token)
        {
            SyncCount++;
            OnSync?.Invoke();
            StatusChanged?.Invoke(this, new(Current));
            return Task.FromResult(new FeedCatalogSyncResult(FeedCatalogSyncOutcome.Updated, 0, Now));
        }
    }

    private sealed class FakeDiscoveryService : IFeedDiscoveryService
    {
        public FeedDiscoveryResult? Result { get; set; }
        public Task<FeedDiscoveryResult> DiscoverAsync(string url, CancellationToken token) =>
            Task.FromResult(Result ?? throw new InvalidOperationException("Missing discovery result."));
    }

    private sealed class FakeAccountSessionService(AccountSessionSnapshot current) : IAccountSessionService
    {
        public bool IsConfigured => true;
        public AccountSessionSnapshot Current { get; private set; } = current;
        public event EventHandler<AccountSessionChangedEventArgs>? SessionChanged
        {
            add { }
            remove { }
        }
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task LoginAsync(string username, string password, CancellationToken token) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken token) => Task.CompletedTask;
    }
}
