using LenxTool.App.ViewModels;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

/// <summary>
/// 冻结 DISC-05 从候选确认到共享目录刷新的发布闭环。
/// </summary>
public sealed class FeedDiscoveryPublishingViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
    private const string FeedId =
        "10000000-0000-4000-8000-000000000020";
    private const string CategoryId =
        "10000000-0000-4000-8000-000000000010";

    [Fact]
    public async Task PublishRequiresExplicitConfirmationAndShowsEveryPolicy()
    {
        TestContext context = CreateContext(Snapshot(7));
        await SearchAsync(context.ViewModel);
        FeedDiscoveryCandidateViewModel candidate =
            Assert.Single(context.ViewModel.Candidates);

        context.ViewModel.PreparePublishCommand.Execute(candidate);

        Assert.True(context.ViewModel.HasPublishSelection);
        Assert.False(context.ViewModel.IsExistingSelection);
        Assert.Equal(
            "https://feeds.example/feed.xml",
            context.ViewModel.PublishNormalizedUrl);
        Assert.Equal("未分类", context.ViewModel.PublishCategoryText);
        Assert.Equal("每 60 分钟刷新", context.ViewModel.PublishRefreshText);
        Assert.Equal("自动识别（默认文章）", context.ViewModel.PublishViewText);
        Assert.Equal("不抓取全文", context.ViewModel.PublishFullTextText);
        Assert.False(context.ViewModel.PublishCommand.CanExecute(null));

        context.ViewModel.IsPublishConfirmed = true;

        Assert.True(context.ViewModel.PublishCommand.CanExecute(null));
    }

    [Fact]
    public async Task PublishUsesCurrentVersionOnceThenRefreshesAndMarksExisting()
    {
        var repository = new FakeCatalogRepository
        {
            Snapshot = Snapshot(7)
        };
        var sync = new FakeCatalogSyncService(() =>
        {
            repository.Snapshot = Snapshot(8, ExistingFeed());
        });
        TestContext context = CreateContext(
            repository,
            sync,
            new FakeCatalogAdminService { NextVersion = 8 });
        await SearchAsync(context.ViewModel);
        context.ViewModel.PreparePublishCommand.Execute(
            Assert.Single(context.ViewModel.Candidates));
        context.ViewModel.SelectedPublishCategory =
            context.ViewModel.PublishCategories.Single(
                item => item.Id == CategoryId);
        context.ViewModel.SelectedPublishRefreshMinutes = 120;
        context.ViewModel.SelectedPublishView =
            context.ViewModel.PublishViewChoices.Single(
                item => item.Kind == FeedViewKind.Picture);
        context.ViewModel.SelectedPublishFullText =
            context.ViewModel.PublishFullTextChoices.Single(
                item => item.Policy == FeedFullTextPolicy.Background);
        context.ViewModel.IsPublishConfirmed = true;

        await context.ViewModel.PublishCommand.ExecuteAsync();

        FakeCatalogAdminService.Call call =
            Assert.Single(context.Admin.Calls);
        Assert.Equal(7, call.ExpectedVersion);
        Assert.Equal(CategoryId, call.Input.CategoryId);
        Assert.Equal(120, call.Input.RefreshIntervalMinutes);
        Assert.Equal(FeedViewKind.Picture, call.Input.ViewKind);
        Assert.True(call.Input.IsViewKindExplicit);
        Assert.Equal(
            FeedFullTextPolicy.Background,
            call.Input.FullTextPolicy);
        Assert.Equal(1, context.Sync.Calls);
        Assert.Equal(8, context.ViewModel.CatalogVersion);
        FeedDiscoveryCandidateViewModel updated =
            Assert.Single(context.ViewModel.Candidates);
        Assert.True(updated.IsExisting);
        Assert.Equal("查看现有项", updated.PrimaryActionLabel);
        Assert.True(context.ViewModel.IsExistingSelection);
        Assert.Contains("v8", context.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentDuplicateSubmitOnlyReachesAdminServiceOnce()
    {
        var repository = new FakeCatalogRepository
        {
            Snapshot = Snapshot(7)
        };
        var sync = new FakeCatalogSyncService(() =>
        {
            repository.Snapshot = Snapshot(8, ExistingFeed());
        });
        var completion =
            new TaskCompletionSource<long>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var admin = new FakeCatalogAdminService
        {
            PendingResult = completion
        };
        TestContext context = CreateContext(repository, sync, admin);
        await SearchAsync(context.ViewModel);
        context.ViewModel.PreparePublishCommand.Execute(
            Assert.Single(context.ViewModel.Candidates));
        context.ViewModel.IsPublishConfirmed = true;

        Task first = context.ViewModel.PublishCommand.ExecuteAsync();
        Task duplicate = context.ViewModel.PublishCommand.ExecuteAsync();

        Assert.Single(admin.Calls);
        Assert.True(duplicate.IsCompletedSuccessfully);

        completion.SetResult(8);
        await first;

        Assert.Single(admin.Calls);
        Assert.True(Assert.Single(context.ViewModel.Candidates).IsExisting);
    }

    [Fact]
    public async Task DuplicateCandidateOnlyOffersExistingItemDetails()
    {
        TestContext context = CreateContext(
            Snapshot(9, ExistingFeed()));
        await SearchAsync(context.ViewModel);
        FeedDiscoveryCandidateViewModel candidate =
            Assert.Single(context.ViewModel.Candidates);

        Assert.True(candidate.IsExisting);
        Assert.Equal("查看现有项", candidate.PrimaryActionLabel);

        context.ViewModel.PreparePublishCommand.Execute(candidate);

        Assert.True(context.ViewModel.IsExistingSelection);
        Assert.Equal("技术", context.ViewModel.PublishCategoryText);
        Assert.Equal("每 120 分钟刷新", context.ViewModel.PublishRefreshText);
        Assert.Equal("图片", context.ViewModel.PublishViewText);
        Assert.Equal("后台自动抓取", context.ViewModel.PublishFullTextText);
        Assert.False(context.ViewModel.PublishCommand.CanExecute(null));
        Assert.Empty(context.Admin.Calls);
    }

    [Fact]
    public async Task ConflictRefreshesCatalogAndNeverReplaysMutation()
    {
        var repository = new FakeCatalogRepository
        {
            Snapshot = Snapshot(12)
        };
        var sync = new FakeCatalogSyncService(() =>
        {
            repository.Snapshot = Snapshot(13, ExistingFeed());
        });
        var admin = new FakeCatalogAdminService
        {
            Failure = Conflict()
        };
        TestContext context = CreateContext(repository, sync, admin);
        await SearchAsync(context.ViewModel);
        context.ViewModel.PreparePublishCommand.Execute(
            Assert.Single(context.ViewModel.Candidates));
        context.ViewModel.IsPublishConfirmed = true;

        await context.ViewModel.PublishCommand.ExecuteAsync();

        Assert.Single(admin.Calls);
        Assert.Equal(1, sync.Calls);
        Assert.Equal(13, context.ViewModel.CatalogVersion);
        Assert.True(Assert.Single(context.ViewModel.Candidates).IsExisting);
        Assert.Contains(
            "其他管理员",
            context.ViewModel.Status,
            StringComparison.Ordinal);
        Assert.False(context.ViewModel.PublishCommand.CanExecute(null));
    }

    [Fact]
    public async Task InterruptedPublishRequiresCatalogRefreshBeforeAnotherWrite()
    {
        var repository = new FakeCatalogRepository
        {
            Snapshot = Snapshot(20)
        };
        var sync = new FakeCatalogSyncService();
        var admin = new FakeCatalogAdminService
        {
            Failure = new AppException(new(
                AppErrorCode.NetworkUnavailable,
                "网络连接中断",
                "写入结果未知。",
                "请刷新目录确认。",
                IsRetryable: true))
        };
        TestContext context = CreateContext(repository, sync, admin);
        await SearchAsync(context.ViewModel);
        context.ViewModel.PreparePublishCommand.Execute(
            Assert.Single(context.ViewModel.Candidates));
        context.ViewModel.IsPublishConfirmed = true;

        await context.ViewModel.PublishCommand.ExecuteAsync();

        Assert.Single(admin.Calls);
        Assert.False(context.ViewModel.IsCatalogCurrent);
        Assert.False(context.ViewModel.IsPublishConfirmed);
        Assert.False(context.ViewModel.PublishCommand.CanExecute(null));
        Assert.Contains("结果未知", context.ViewModel.Status, StringComparison.Ordinal);

        admin.Failure = null;
        await context.ViewModel.RefreshCatalogCommand.ExecuteAsync();

        Assert.Equal(1, sync.Calls);
        Assert.True(context.ViewModel.IsCatalogCurrent);
        Assert.Single(admin.Calls);
    }

    [Fact]
    public async Task ServerAccessDeniedRemainsAuthoritativeAndIsNotRetried()
    {
        var admin = new FakeCatalogAdminService
        {
            Failure = new AppException(new(
                AppErrorCode.AccessDenied,
                "没有访问权限",
                "服务端拒绝目录写入。",
                "请检查管理员账号。"))
        };
        TestContext context = CreateContext(
            new FakeCatalogRepository { Snapshot = Snapshot(30) },
            new FakeCatalogSyncService(),
            admin);
        await SearchAsync(context.ViewModel);
        context.ViewModel.PreparePublishCommand.Execute(
            Assert.Single(context.ViewModel.Candidates));
        context.ViewModel.IsPublishConfirmed = true;

        await context.ViewModel.PublishCommand.ExecuteAsync();

        Assert.Single(admin.Calls);
        Assert.False(context.ViewModel.IsPublishConfirmed);
        Assert.Contains("没有访问权限", context.ViewModel.Status, StringComparison.Ordinal);
    }

    private static async Task SearchAsync(
        FeedDiscoveryViewModel viewModel)
    {
        viewModel.Input = "reader";
        await viewModel.SearchCommand.ExecuteAsync();
        Assert.Equal(FeedDiscoveryPageState.Ready, viewModel.State);
    }

    private static TestContext CreateContext(
        FeedCatalogSnapshot snapshot)
    {
        var repository = new FakeCatalogRepository
        {
            Snapshot = snapshot
        };
        return CreateContext(
            repository,
            new FakeCatalogSyncService(),
            new FakeCatalogAdminService());
    }

    private static TestContext CreateContext(
        FakeCatalogRepository repository,
        FakeCatalogSyncService sync,
        FakeCatalogAdminService admin)
    {
        var account = new FakeAccountSession();
        var viewModel = new FeedDiscoveryViewModel(
            new FakeUnifiedDiscoveryService(),
            repository,
            new EmptyPreviewRepository(),
            account,
            admin,
            sync,
            TimeSpan.FromHours(1));
        return new(viewModel, repository, sync, admin, account);
    }

    private static FeedCatalogSnapshot Snapshot(
        long version,
        params FeedCatalogItem[] feeds) =>
        new(
            new(version, FeedCatalogScope.All, Now, Now),
            [
                new(
                    CategoryId,
                    "技术",
                    "技术",
                    100,
                    true,
                    version,
                    Now,
                    Now)
            ],
            feeds);

    private static FeedCatalogItem ExistingFeed() =>
        new(
            FeedId,
            "https://feeds.example/feed.xml",
            "https://feeds.example/feed.xml",
            "示例订阅",
            "https://feeds.example/",
            CategoryId,
            FeedViewKind.Picture,
            120,
            100,
            true,
            8,
            Now,
            Now,
            FeedFullTextPolicy.Background,
            IsViewKindExplicit: true);

    private static AppException Conflict() =>
        new(new(
            AppErrorCode.Conflict,
            "数据版本冲突",
            "目录已被其他管理员更新。",
            "请刷新后重试。",
            "Worker error code: CATALOG_VERSION_CONFLICT"));

    private sealed record TestContext(
        FeedDiscoveryViewModel ViewModel,
        FakeCatalogRepository Repository,
        FakeCatalogSyncService Sync,
        FakeCatalogAdminService Admin,
        FakeAccountSession Account);

    private sealed class FakeUnifiedDiscoveryService
        : IUnifiedFeedDiscoveryService
    {
        public Task<UnifiedFeedDiscoveryResult> DiscoverAsync(
            string input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UnifiedFeedDiscoveryResult(
                new(
                    input,
                    FeedDiscoveryQueryKind.Keyword,
                    FeedDiscoveryQueryError.None),
                [
                    new(
                        "https://feeds.example/feed.xml",
                        "示例订阅",
                        "https://feeds.example/",
                        FeedDocumentKind.Rss20,
                        Now,
                        FeedDiscoveryHealth.Healthy,
                        [
                            new(
                                "worker:known-catalog",
                                FeedDiscoverySourceKind.KnownCatalog,
                                FeedDiscoveryMatchKind.Keyword,
                                FeedDiscoveryConfidence.High)
                        ],
                        [])
                ],
                [
                    new(
                        "worker:known-catalog",
                        FeedDiscoverySourceKind.KnownCatalog,
                        FeedDiscoverySourceStatus.Succeeded,
                        1,
                        false,
                        false)
                ],
                FeedDiscoveryCompletionStatus.Complete));
    }

    private sealed class FakeCatalogRepository : IFeedCatalogRepository
    {
        public FeedCatalogSnapshot? Snapshot { get; set; }

        public Task ReplaceAsync(
            FeedCatalogSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Snapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Snapshot?.State
                ?? new FeedCatalogState(
                    0,
                    FeedCatalogScope.Active,
                    null,
                    null));
    }

    private sealed class FakeCatalogAdminService
        : IFeedCatalogAdminService
    {
        public sealed record Call(
            FeedCatalogItemInput Input,
            long ExpectedVersion);

        public List<Call> Calls { get; } = [];
        public long NextVersion { get; set; }
        public AppException? Failure { get; set; }
        public TaskCompletionSource<long>? PendingResult { get; set; }

        public Task<long> CreateFeedAsync(
            FeedCatalogItemInput input,
            long expectedCatalogVersion,
            CancellationToken cancellationToken)
        {
            Calls.Add(new(input, expectedCatalogVersion));
            if (PendingResult is not null) return PendingResult.Task;
            return Failure is null
                ? Task.FromResult(
                    NextVersion == 0
                        ? expectedCatalogVersion + 1
                        : NextVersion)
                : Task.FromException<long>(Failure);
        }

        public Task<long> CreateCategoryAsync(
            FeedCategoryInput input,
            long expectedCatalogVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> UpdateCategoryAsync(
            string categoryId,
            FeedCategoryInput input,
            long expectedCatalogVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> DeleteCategoryAsync(
            string categoryId,
            long expectedCatalogVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> UpdateFeedAsync(
            string feedId,
            FeedCatalogItemInput input,
            long expectedCatalogVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> DeleteFeedAsync(
            string feedId,
            long expectedCatalogVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCatalogSyncService(Action? synchronize = null)
        : IFeedCatalogSyncService
    {
        public int Calls { get; private set; }
        public FeedCatalogSyncStatus Current { get; } =
            new(false, 0, FeedCatalogScope.All, Now, false, 0, null, null);
        public event EventHandler<FeedCatalogSyncStatusChangedEventArgs>?
            StatusChanged
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<FeedCatalogSyncResult> SyncAsync(
            CancellationToken cancellationToken)
        {
            Calls++;
            synchronize?.Invoke();
            return Task.FromResult(
                new FeedCatalogSyncResult(
                    FeedCatalogSyncOutcome.Updated,
                    Current.Version,
                    Now));
        }
    }

    private sealed class EmptyPreviewRepository
        : IFeedDiscoveryPreviewRepository
    {
        public Task<IReadOnlyList<FeedDiscoveryPreviewItem>> GetRecentAsync(
            IReadOnlyCollection<string> feedIds,
            int maximumPerFeed,
            string localProfile,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FeedDiscoveryPreviewItem>>([]);
    }

    private sealed class FakeAccountSession : IAccountSessionService
    {
        public bool IsConfigured => true;
        public AccountSessionSnapshot Current { get; private set; } =
            new(
                AccountSessionStatus.SignedIn,
                new("user-1", "owner", AccountRole.Admin));
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
