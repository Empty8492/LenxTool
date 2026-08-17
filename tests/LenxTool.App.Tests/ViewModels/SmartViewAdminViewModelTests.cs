using LenxTool.App.ViewModels;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class SmartViewAdminViewModelTests
{
    private const string CategoryId =
        "10000000-0000-4000-8000-000000000001";
    private const string FeedId =
        "20000000-0000-4000-8000-000000000001";
    private const string ViewId =
        "30000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        28,
        10,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task AdminLoadsAllViewsAndClosedCatalogChoices()
    {
        TestContext context = CreateContext(AccountRole.Admin);

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        FeedSmartView selected = Assert.Single(
            context.ViewModel.SmartViews);
        Assert.Same(selected, context.ViewModel.SelectedSmartView);
        Assert.Equal(7, context.ViewModel.ViewSetVersion);
        Assert.Equal("视频收藏", context.ViewModel.ViewName);
        Assert.Equal(
            FeedId,
            context.ViewModel.SelectedFeed.Id);
        Assert.Equal(
            CategoryId,
            context.ViewModel.SelectedCategory.Id);
        Assert.Equal(
            EntryViewKind.Video,
            context.ViewModel.SelectedViewKind.Value);
        Assert.Equal(
            FeedEntryReadFilter.Unread,
            context.ViewModel.SelectedReadFilter.Value);
        Assert.True(context.ViewModel.FavoritesOnly);
        Assert.DoesNotContain(
            context.ViewModel.GetType().GetProperties(),
            property => property.Name.Contains(
                "Script",
                StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "Url",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "Content",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OrdinaryUserCannotLoadOrPublishSharedViews()
    {
        TestContext context = CreateContext(AccountRole.User);

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.False(context.ViewModel.IsAdmin);
        Assert.Empty(context.ViewModel.SmartViews);
        Assert.False(context.ViewModel.RefreshCommand.CanExecute(null));
        Assert.False(context.ViewModel.PublishCommand.CanExecute(null));
        Assert.False(
            context.ViewModel.PrepareDeleteCommand.CanExecute(null));
        Assert.Equal(0, context.Admin.GetCount);
        Assert.Contains("管理员", context.ViewModel.Status);
    }

    [Fact]
    public async Task PublishingNewViewUsesCurrentVersionAndRefreshesActiveCache()
    {
        TestContext context = CreateContext(AccountRole.Admin);
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.BeginNewCommand.Execute(null);
        context.ViewModel.ViewName = "近期文章";
        context.ViewModel.SortOrder = 30;
        context.ViewModel.SelectedCategory =
            context.ViewModel.CategoryChoices.Single(
                choice => choice.Id == CategoryId);
        context.ViewModel.SelectedViewKind =
            context.ViewModel.ViewKindChoices.Single(
                choice => choice.Value == EntryViewKind.Article);
        context.ViewModel.SelectedReadFilter =
            context.ViewModel.ReadFilterChoices.Single(
                choice => choice.Value == FeedEntryReadFilter.Read);
        context.ViewModel.PublishedWithinDays = 14;
        context.Admin.NextMutation = new(
            8,
            View() with
            {
                Version = 1,
                Name = "近期文章",
                SortOrder = 30,
                Filter = View().Filter with
                {
                    ViewKind = EntryViewKind.Article,
                    ReadFilter = FeedEntryReadFilter.Read,
                    PublishedWithinDays = 14
                }
            });

        await context.ViewModel.PublishCommand.ExecuteAsync();

        SmartViewMutationCall call = Assert.Single(
            context.Admin.CreateCalls);
        Assert.Equal(7, call.ExpectedVersion);
        Assert.Equal("近期文章", call.Input.Name);
        Assert.Equal(CategoryId, call.Input.Filter.CategoryId);
        Assert.Equal(EntryViewKind.Article, call.Input.Filter.ViewKind);
        Assert.Equal(FeedEntryReadFilter.Read, call.Input.Filter.ReadFilter);
        Assert.Equal(14, call.Input.Filter.PublishedWithinDays);
        Assert.Equal(8, context.ViewModel.ViewSetVersion);
        Assert.Equal("近期文章", context.ViewModel.SelectedSmartView?.Name);
        Assert.Equal(1, context.Sync.Count);
        Assert.Contains("只读", context.ViewModel.Status);
    }

    [Fact]
    public async Task VersionConflictRefreshesWithoutReplayingMutation()
    {
        TestContext context = CreateContext(AccountRole.Admin);
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.Admin.MutationFailure = Conflict();
        context.Admin.Snapshot = Snapshot() with { ViewSetVersion = 8 };
        context.ViewModel.SelectedSmartView =
            Assert.Single(context.ViewModel.SmartViews);

        await context.ViewModel.PublishCommand.ExecuteAsync();

        Assert.Single(context.Admin.UpdateCalls);
        Assert.Equal(2, context.Admin.GetCount);
        Assert.Equal(8, context.ViewModel.ViewSetVersion);
        Assert.Equal(0, context.Sync.Count);
        Assert.Contains("其他管理员", context.ViewModel.Status);
        Assert.Contains("重试", context.ViewModel.Status);
    }

    [Fact]
    public async Task DeleteRequiresConfirmationAndUsesCurrentVersion()
    {
        TestContext context = CreateContext(AccountRole.Admin);
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.SelectedSmartView =
            Assert.Single(context.ViewModel.SmartViews);

        context.ViewModel.PrepareDeleteCommand.Execute(null);

        Assert.True(context.ViewModel.IsDeletePending);
        Assert.Empty(context.Admin.DeleteCalls);

        await context.ViewModel.ConfirmDeleteCommand.ExecuteAsync();

        SmartViewDeleteCall call = Assert.Single(
            context.Admin.DeleteCalls);
        Assert.Equal(ViewId, call.ViewId);
        Assert.Equal(7, call.ExpectedVersion);
        Assert.Equal(8, context.ViewModel.ViewSetVersion);
        Assert.Empty(context.ViewModel.SmartViews);
        Assert.False(context.ViewModel.IsDeletePending);
        Assert.Equal(1, context.Sync.Count);
    }

    [Fact]
    public async Task RoleLossClearsViewsAndDisablesMutations()
    {
        TestContext context = CreateContext(AccountRole.Admin);
        await context.ViewModel.InitializeAsync(CancellationToken.None);

        context.Account.SetRole(AccountRole.User);

        Assert.False(context.ViewModel.IsAdmin);
        Assert.Empty(context.ViewModel.SmartViews);
        Assert.Equal(0, context.ViewModel.ViewSetVersion);
        Assert.False(context.ViewModel.PublishCommand.CanExecute(null));
        Assert.False(
            context.ViewModel.ConfirmDeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task RoleLossDuringPublishDoesNotRepopulateAdminState()
    {
        TestContext context = CreateContext(AccountRole.Admin);
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.BeginNewCommand.Execute(null);
        context.ViewModel.ViewName = "待发布视图";
        var pending = new TaskCompletionSource<FeedSmartViewMutationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Admin.PendingMutation = pending;

        Task publish = context.ViewModel.PublishCommand.ExecuteAsync();
        await context.Admin.MutationStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        context.Account.SetRole(AccountRole.User);
        pending.SetResult(new(
            8,
            View() with
            {
                Version = 1,
                Name = "待发布视图"
            }));
        await publish;

        Assert.False(context.ViewModel.IsAdmin);
        Assert.Empty(context.ViewModel.SmartViews);
        Assert.Equal(0, context.ViewModel.ViewSetVersion);
        Assert.Null(context.ViewModel.SelectedSmartView);
        Assert.Equal(0, context.Sync.Count);
    }

    [Fact]
    public async Task RoleLossDuringCacheSyncKeepsAdminStateCleared()
    {
        TestContext context = CreateContext(AccountRole.Admin);
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.BeginNewCommand.Execute(null);
        context.ViewModel.ViewName = "已提交视图";
        var pending = new TaskCompletionSource<FeedSmartViewSyncResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Sync.PendingResult = pending;

        Task publish = context.ViewModel.PublishCommand.ExecuteAsync();
        await context.Sync.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        context.Account.SetRole(AccountRole.User);
        pending.SetResult(new(
            FeedSmartViewSyncOutcome.Updated,
            8,
            Now));
        await publish;

        Assert.False(context.ViewModel.IsAdmin);
        Assert.Empty(context.ViewModel.SmartViews);
        Assert.Equal(0, context.ViewModel.ViewSetVersion);
        Assert.Contains("管理员", context.ViewModel.Status);
    }

    private static TestContext CreateContext(AccountRole role)
    {
        var admin = new FakeAdminService();
        var sync = new FakeSyncService();
        var account = new FakeAccountSession(role);
        var viewModel = new SmartViewAdminViewModel(
            admin,
            sync,
            new FakeCatalogRepository(),
            account);
        return new(viewModel, admin, sync, account);
    }

    private static FeedSmartViewSnapshot Snapshot() => new(
        7,
        FeedSmartViewScope.All,
        Now,
        null,
        [View()]);

    private static FeedSmartView View() => new(
        ViewId,
        2,
        "视频收藏",
        20,
        true,
        new(
            FeedId,
            CategoryId,
            EntryViewKind.Video,
            FeedEntryReadFilter.Unread,
            true,
            "release",
            30));

    private static FeedCatalogSnapshot Catalog() => new(
        new(
            5,
            FeedCatalogScope.Active,
            Now,
            Now),
        [
            new(
                CategoryId,
                "技术",
                "technology",
                10,
                true,
                5,
                Now,
                Now)
        ],
        [
            new(
                FeedId,
                "https://feeds.example/rss.xml",
                "https://feeds.example/rss.xml",
                "示例 Feed",
                "https://feeds.example/",
                CategoryId,
                FeedViewKind.Article,
                60,
                10,
                true,
                5,
                Now,
                Now)
        ]);

    private static AppException Conflict() => new(new(
        AppErrorCode.Conflict,
        "智能视图版本冲突",
        "其他管理员已经修改智能视图",
        "刷新后重试",
        "SMART_VIEW_VERSION_CONFLICT"));

    private sealed record TestContext(
        SmartViewAdminViewModel ViewModel,
        FakeAdminService Admin,
        FakeSyncService Sync,
        FakeAccountSession Account);

    private sealed record SmartViewMutationCall(
        string? ViewId,
        FeedSmartViewInput Input,
        long ExpectedVersion);

    private sealed record SmartViewDeleteCall(
        string ViewId,
        long ExpectedVersion);

    private sealed class FakeAdminService
        : IFeedSmartViewAdminService
    {
        public FeedSmartViewSnapshot Snapshot { get; set; } =
            SmartViewAdminViewModelTests.Snapshot();
        public FeedSmartViewMutationResult NextMutation { get; set; } =
            new(8, View());
        public AppException? MutationFailure { get; set; }
        public TaskCompletionSource<FeedSmartViewMutationResult>?
            PendingMutation
        { get; set; }
        public TaskCompletionSource MutationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int GetCount { get; private set; }
        public List<SmartViewMutationCall> CreateCalls { get; } = [];
        public List<SmartViewMutationCall> UpdateCalls { get; } = [];
        public List<SmartViewDeleteCall> DeleteCalls { get; } = [];

        public Task<FeedSmartViewSnapshot> GetAllAsync(
            CancellationToken cancellationToken)
        {
            GetCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<FeedSmartViewMutationResult> CreateAsync(
            FeedSmartViewInput input,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            CreateCalls.Add(new(null, input, expectedVersion));
            return Mutation();
        }

        public Task<FeedSmartViewMutationResult> UpdateAsync(
            string viewId,
            FeedSmartViewInput input,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            UpdateCalls.Add(new(viewId, input, expectedVersion));
            return Mutation();
        }

        public Task<FeedSmartViewMutationResult> DeleteAsync(
            string viewId,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            DeleteCalls.Add(new(viewId, expectedVersion));
            if (MutationFailure is not null)
            {
                throw MutationFailure;
            }
            return Task.FromResult(
                new FeedSmartViewMutationResult(
                    expectedVersion + 1,
                    null,
                    viewId));
        }

        private Task<FeedSmartViewMutationResult> Mutation()
        {
            MutationStarted.TrySetResult();
            if (MutationFailure is not null)
            {
                throw MutationFailure;
            }
            return PendingMutation?.Task
                ?? Task.FromResult(NextMutation);
        }
    }

    private sealed class FakeSyncService
        : IFeedSmartViewSyncService
    {
        public int Count { get; private set; }
        public TaskCompletionSource<FeedSmartViewSyncResult>?
            PendingResult
        { get; set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FeedSmartViewSyncResult> SyncAsync(
            CancellationToken cancellationToken)
        {
            Count++;
            Started.TrySetResult();
            return PendingResult?.Task
                ?? Task.FromResult(new FeedSmartViewSyncResult(
                    FeedSmartViewSyncOutcome.Updated,
                    8,
                    Now));
        }
    }

    private sealed class FakeCatalogRepository
        : IFeedCatalogRepository
    {
        public Task ReplaceAsync(
            FeedCatalogSnapshot snapshot,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(Catalog());

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Catalog().State);
    }

    private sealed class FakeAccountSession(AccountRole role)
        : IAccountSessionService
    {
        public bool IsConfigured => true;
        public AccountSessionSnapshot Current { get; private set; } =
            SignedIn(role);
        public event EventHandler<AccountSessionChangedEventArgs>?
            SessionChanged;

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

        public void SetRole(AccountRole value)
        {
            Current = SignedIn(value);
            SessionChanged?.Invoke(this, new(Current));
        }
    }

    private static AccountSessionSnapshot SignedIn(
        AccountRole role) => new(
        AccountSessionStatus.SignedIn,
        new(
            "50000000-0000-4000-8000-000000000001",
            "owner",
            role));
}
