using LenxTool.App.ViewModels;
using LenxTool.App.Services;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class FeedAdminViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OpmlPreviewNeverSubmitsAndClassifiesItemsAgainstCurrentCatalog()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.OpmlFiles.Loaded = new(
            "导入",
            [
                new("新订阅", "https://new.example/feed.xml", null, ["技术", "开发"]),
                new("示例源", Snapshot(7).Feeds[0].OriginalUrl, null, ["技术"]),
                new("不安全", "http://unsafe.example/feed.xml", null, [])
            ]);
        context.Dialogs.ImportPath = "selected.opml";
        await context.ViewModel.InitializeAsync(CancellationToken.None);

        await context.ViewModel.PreviewOpmlCommand.ExecuteAsync();

        Assert.Equal(3, context.ViewModel.OpmlItems.Count);
        Assert.Equal(OpmlCatalogItemStatus.New, context.ViewModel.OpmlItems[0].Status);
        Assert.Equal(OpmlCatalogItemStatus.Duplicate, context.ViewModel.OpmlItems[1].Status);
        Assert.Equal(OpmlCatalogItemStatus.Invalid, context.ViewModel.OpmlItems[2].Status);
        Assert.Empty(context.Batch.Calls);
        Assert.Equal(1, context.ViewModel.SelectedOpmlCount);
    }

    [Fact]
    public async Task SelectedOpmlIsDiscoveredThenSubmittedAsOneCategoryReferenceBatch()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.OpmlFiles.Loaded = new(
            "导入",
            [new("新订阅", "https://new.example/feed.xml", "https://new.example/", ["技术", "开发"])]);
        context.Dialogs.ImportPath = "selected.opml";
        context.Discovery.Result = new(
            "https://new.example/feed.xml",
            [new("https://new.example/feed.xml", "发现标题", FeedDocumentKind.Atom)]);
        context.Sync.OnSync = () => context.Repository.Snapshot = Snapshot(8);
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        await context.ViewModel.PreviewOpmlCommand.ExecuteAsync();

        await context.ViewModel.ImportSelectedOpmlCommand.ExecuteAsync();

        BatchCall call = Assert.Single(context.Batch.Calls);
        Assert.Equal(7, call.ExpectedVersion);
        Assert.Collection(
            call.Operations,
            category =>
            {
                Assert.Equal(FeedCatalogBatchOperationType.CreateCategory, category.Type);
                Assert.Equal("技术 / 开发", category.CategoryInput!.Name);
            },
            feed =>
            {
                Assert.Equal(FeedCatalogBatchOperationType.CreateFeed, feed.Type);
                Assert.Equal("category-1", feed.CategoryOperationId);
                Assert.Equal("https://new.example/feed.xml", feed.FeedInput!.OriginalUrl);
            });
        Assert.Equal(1, context.Sync.SyncCount);
        Assert.Empty(context.ViewModel.OpmlItems);
        Assert.Contains("原子导入", context.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpmlExportContainsOnlyCatalogProjection()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.Dialogs.ExportPath = "export.opml";
        await context.ViewModel.InitializeAsync(CancellationToken.None);

        await context.ViewModel.ExportOpmlCommand.ExecuteAsync();

        Assert.NotNull(context.OpmlFiles.Saved);
        OpmlDocument exported = context.OpmlFiles.Saved;
        Assert.Equal("LenxTool 共享订阅 v7", exported.Title);
        OpmlFeed feed = Assert.Single(exported.Feeds);
        Assert.Equal("示例源", feed.Title);
        Assert.Equal(["技术"], feed.GroupPath);
    }

    [Fact]
    public async Task OpmlDiscoveryFailureMarksItemInvalidAndDoesNotSubmitBatch()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.OpmlFiles.Loaded = new(
            "导入",
            [new("失败订阅", "https://failed.example/feed.xml", null, [])]);
        context.Dialogs.ImportPath = "selected.opml";
        context.Discovery.Failure = new AppException(new(
            AppErrorCode.ProviderUnavailable,
            "发现失败",
            "无法读取订阅",
            "检查地址后重试"));
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        await context.ViewModel.PreviewOpmlCommand.ExecuteAsync();

        await context.ViewModel.ImportSelectedOpmlCommand.ExecuteAsync();

        Assert.Empty(context.Batch.Calls);
        Assert.Equal(OpmlCatalogItemStatus.Invalid, Assert.Single(context.ViewModel.OpmlItems).Status);
        Assert.Contains("未提交任何", context.ViewModel.Status, StringComparison.Ordinal);
    }

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
    public async Task CategoryEditorLoadsAndWritesAiPolicyOverrides()
    {
        FeedAiPolicy storedPolicy = FeedAiPolicy.Inherited with
        {
            ManualSummary = FeedAiPolicySwitch.Disabled,
            AutoSummary = FeedAiPolicySwitch.Enabled,
            DailyEntryLimit = 12
        };
        FeedCatalogSnapshot snapshot = Snapshot(7) with
        {
            Categories = [Snapshot(7).Categories[0] with { AiPolicy = storedPolicy }]
        };
        var context = CreateViewModel(snapshot, AccountRole.Admin);
        context.Sync.OnSync = () => context.Repository.Snapshot = Snapshot(8);
        await context.ViewModel.InitializeAsync(CancellationToken.None);

        context.ViewModel.SelectedCategory = context.ViewModel.Categories[0];

        Assert.Equal(FeedAiPolicySwitch.Disabled, context.ViewModel.CategoryManualSummaryPolicy);
        Assert.Equal(FeedAiPolicySwitch.Enabled, context.ViewModel.CategoryAutoSummaryPolicy);
        Assert.Equal(12, context.ViewModel.CategoryAiDailyEntryLimit);
        Assert.Contains("12", context.ViewModel.CategoryAiUsageEstimate, StringComparison.Ordinal);
        Assert.Contains("并发 1", context.ViewModel.CategoryAiUsageEstimate, StringComparison.Ordinal);

        context.ViewModel.CategoryAutoTranslationPolicy = FeedAiPolicySwitch.Enabled;
        context.ViewModel.CategoryTranslationTargetLanguage = "ja";
        context.ViewModel.CategoryAiMaxConcurrency = 2;
        await context.ViewModel.SaveCategoryCommand.ExecuteAsync();

        FeedAiPolicy saved = Assert.IsType<FeedAiPolicy>(
            Assert.Single(context.Admin.CategoryCalls).Input?.AiPolicy);
        Assert.Equal(FeedAiPolicySwitch.Disabled, saved.ManualSummary);
        Assert.Equal(FeedAiPolicySwitch.Enabled, saved.AutoSummary);
        Assert.Equal(FeedAiPolicySwitch.Enabled, saved.AutoTranslation);
        Assert.Equal("ja", saved.TranslationTargetLanguage);
        Assert.Equal(12, saved.DailyEntryLimit);
        Assert.Equal(2, saved.MaxConcurrency);
    }

    [Fact]
    public async Task FeedEditorLoadsAndWritesAiPolicyOverrides()
    {
        FeedAiPolicy storedPolicy = FeedAiPolicy.Inherited with
        {
            AutoTranslation = FeedAiPolicySwitch.Enabled,
            TranslationTargetLanguage = "ko",
            MaxConcurrency = 3
        };
        FeedCatalogSnapshot snapshot = Snapshot(7) with
        {
            Feeds = [Snapshot(7).Feeds[0] with { AiPolicy = storedPolicy }]
        };
        var context = CreateViewModel(snapshot, AccountRole.Admin);
        context.Sync.OnSync = () => context.Repository.Snapshot = Snapshot(8);
        await context.ViewModel.InitializeAsync(CancellationToken.None);

        context.ViewModel.SelectedFeed = context.ViewModel.Feeds[0];

        Assert.Equal(FeedAiPolicySwitch.Enabled, context.ViewModel.FeedAutoTranslationPolicy);
        Assert.Equal("ko", context.ViewModel.FeedTranslationTargetLanguage);
        Assert.Equal(3, context.ViewModel.FeedAiMaxConcurrency);
        Assert.Contains("20", context.ViewModel.FeedAiUsageEstimate, StringComparison.Ordinal);
        Assert.Contains("并发 3", context.ViewModel.FeedAiUsageEstimate, StringComparison.Ordinal);

        context.ViewModel.FeedManualSummaryPolicy = FeedAiPolicySwitch.Disabled;
        context.ViewModel.FeedAutoSummaryPolicy = FeedAiPolicySwitch.Enabled;
        context.ViewModel.FeedAiDailyEntryLimit = 8;
        await context.ViewModel.SaveFeedCommand.ExecuteAsync();

        FeedAiPolicy saved = Assert.IsType<FeedAiPolicy>(
            Assert.Single(context.Admin.FeedCalls).Input?.AiPolicy);
        Assert.Equal(FeedAiPolicySwitch.Disabled, saved.ManualSummary);
        Assert.Equal(FeedAiPolicySwitch.Enabled, saved.AutoSummary);
        Assert.Equal(FeedAiPolicySwitch.Enabled, saved.AutoTranslation);
        Assert.Equal("ko", saved.TranslationTargetLanguage);
        Assert.Equal(8, saved.DailyEntryLimit);
        Assert.Equal(3, saved.MaxConcurrency);
    }

    [Fact]
    public async Task AdminInitializationLoadsFetchHealthWithRedactedErrorLabels()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.FetchStates.Targets =
        [
            new(
                Snapshot(7).Feeds[0],
                new(
                    Snapshot(7).Feeds[0].Id,
                    null,
                    null,
                    Now.AddMinutes(20),
                    null,
                    Now,
                    3,
                    "http_503",
                    Now))
        ];

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        FeedHealthItem health = Assert.Single(context.ViewModel.HealthItems);
        Assert.Equal("失败 · 连续 3 次", health.StatusLabel);
        Assert.Equal("HTTP 503", health.ErrorLabel);
        Assert.DoesNotContain("secret", health.ErrorLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("从未成功", health.LastSuccessText, StringComparison.Ordinal);
        Assert.Contains("下次", health.NextRetryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualRetryUsesForcedRefreshAndReportsOutcome()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.FetchStates.Targets =
        [
            new(Snapshot(7).Feeds[0], null)
        ];
        context.Refresh.Results[Snapshot(7).Feeds[0].Id] = new(
            Snapshot(7).Feeds[0].Id,
            FeedRefreshOutcome.Updated,
            4,
            Now.AddHours(1),
            null);

        await context.ViewModel.InitializeAsync(CancellationToken.None);
        FeedHealthItem health = Assert.Single(context.ViewModel.HealthItems);

        await context.ViewModel.RetryFeedCommand.ExecuteAsync(health);

        Assert.Equal([(Snapshot(7).Feeds[0].Id, true)], context.Refresh.Calls);
        Assert.Contains("已抓取 4 条", context.ViewModel.Status, StringComparison.Ordinal);
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
        context.ViewModel.SelectedFullTextPolicy = context.ViewModel.FullTextPolicyChoices.Single(
            choice => choice.Policy == FeedFullTextPolicy.Background);

        await context.ViewModel.SaveFeedCommand.ExecuteAsync();

        FeedCall call = Assert.Single(context.Admin.FeedCalls);
        Assert.Equal("create", call.Operation);
        Assert.Equal(7, call.ExpectedVersion);
        Assert.Equal("https://new.example/feed.xml", call.Input!.OriginalUrl);
        Assert.Equal(FeedFullTextPolicy.Background, call.Input.FullTextPolicy);
        Assert.Equal(8, context.ViewModel.CatalogVersion);
        Assert.Equal(2, context.ViewModel.Feeds.Count);
        Assert.Contains("已保存", context.ViewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulMutationWithFailedSyncLocksFurtherWritesAndReportsCommittedVersion()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.Discovery.Result = new(
            "https://new.example/",
            [new("https://new.example/feed.xml", "新订阅", FeedDocumentKind.Rss20)]);
        context.Sync.Failure = new AppException(new(
            AppErrorCode.NetworkUnavailable,
            "同步失败",
            "目录刷新失败",
            "请稍后刷新",
            "simulated sync failure"));
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.BeginNewFeedCommand.Execute(null);
        context.ViewModel.FeedUrlInput = "https://new.example/";
        await context.ViewModel.DiscoverCommand.ExecuteAsync();

        await context.ViewModel.SaveFeedCommand.ExecuteAsync();

        Assert.Single(context.Admin.FeedCalls);
        Assert.False(context.ViewModel.CanManage);
        Assert.False(context.ViewModel.SaveFeedCommand.CanExecute(null));
        Assert.Contains("远端更改已提交为 v8", context.ViewModel.Status, StringComparison.Ordinal);
        Assert.Contains("本地刷新失败", context.ViewModel.Status, StringComparison.Ordinal);
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
    public async Task RoleLossDuringPostMutationSyncDoesNotRepopulateAdminCatalog()
    {
        var context = CreateViewModel(Snapshot(7), AccountRole.Admin);
        context.Sync.OnSync = () => context.Account.SetSession(SignedIn(AccountRole.User));
        await context.ViewModel.InitializeAsync(CancellationToken.None);
        context.ViewModel.SelectedCategory = context.ViewModel.Categories[0];
        context.ViewModel.CategoryNameInput = "更新后的名称";

        await context.ViewModel.SaveCategoryCommand.ExecuteAsync();

        Assert.Single(context.Admin.CategoryCalls);
        Assert.False(context.ViewModel.IsAdmin);
        Assert.False(context.ViewModel.CanManage);
        Assert.Empty(context.ViewModel.Categories);
        Assert.Empty(context.ViewModel.Feeds);
    }

    [Fact]
    public async Task ToggleMoveAndTwoStepDeleteUseVersionedFeedMutations()
    {
        FeedCatalogSnapshot initial = Snapshot(10) with
        {
            Feeds =
            [
                Feed("10000000-0000-4000-8000-000000000020", "第一源", 100) with
                {
                    AiPolicy = FeedAiPolicy.Inherited with
                    {
                        ManualSummary = FeedAiPolicySwitch.Disabled,
                        DailyEntryLimit = 6
                    }
                },
                Feed("10000000-0000-4000-8000-000000000021", "第二源", 200)
            ]
        };
        var context = CreateViewModel(initial, AccountRole.Admin);
        context.Sync.OnSync = () =>
        {
            FeedCatalogSnapshot current = context.Repository.Snapshot!;
            context.Repository.Snapshot = current with
            {
                State = current.State with { Version = current.State.Version + 1 },
                Feeds = context.Sync.SyncCount == 3
                    ? current.Feeds.Where(feed => feed.Id != initial.Feeds[0].Id).ToArray()
                    : current.Feeds
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
                Assert.Equal(initial.Feeds[0].AiPolicy, call.Input.AiPolicy);
            },
            call =>
            {
                Assert.Equal("update", call.Operation);
                Assert.True(call.Input!.SortOrder > 200);
                Assert.Equal(initial.Feeds[0].AiPolicy, call.Input.AiPolicy);
            },
            call => Assert.Equal("delete", call.Operation));
        Assert.Null(context.ViewModel.SelectedFeed);
        Assert.Empty(context.ViewModel.FeedUrlInput);
        Assert.False(context.ViewModel.HasDiscoveryPreview);
    }

    private static TestContext CreateViewModel(FeedCatalogSnapshot snapshot, AccountRole role)
    {
        var repository = new FakeCatalogRepository { Snapshot = snapshot };
        var sync = new FakeCatalogSyncService();
        var admin = new FakeCatalogAdminService();
        var discovery = new FakeDiscoveryService();
        var account = new FakeAccountSessionService(SignedIn(role));
        var batch = new FakeCatalogBatchService();
        var opmlFiles = new FakeOpmlFileService();
        var dialogs = new FakeOpmlFileDialogs();
        var fetchStates = new FakeFeedFetchStateRepository();
        var refresh = new FakeFeedRefreshService();
        var viewModel = new FeedAdminViewModel(
            admin,
            repository,
            sync,
            discovery,
            account,
            batch,
            opmlFiles,
            dialogs,
            fetchStates,
            refresh);
        return new(viewModel, admin, repository, sync, discovery, account, batch, opmlFiles, dialogs, fetchStates, refresh);
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
        FakeDiscoveryService Discovery,
        FakeAccountSessionService Account,
        FakeCatalogBatchService Batch,
        FakeOpmlFileService OpmlFiles,
        FakeOpmlFileDialogs Dialogs,
        FakeFeedFetchStateRepository FetchStates,
        FakeFeedRefreshService Refresh);

    private sealed record BatchCall(
        IReadOnlyList<FeedCatalogBatchOperation> Operations,
        long ExpectedVersion);

    private sealed class FakeCatalogBatchService : IFeedCatalogBatchService
    {
        public List<BatchCall> Calls { get; } = [];

        public Task<FeedCatalogBatchResult> ApplyAsync(
            IReadOnlyList<FeedCatalogBatchOperation> operations,
            long expectedCatalogVersion,
            CancellationToken cancellationToken)
        {
            Calls.Add(new(operations, expectedCatalogVersion));
            FeedCatalogBatchOperationResult[] results = operations.Select((operation, index) => new FeedCatalogBatchOperationResult(
                operation.OperationId,
                operation.Type is FeedCatalogBatchOperationType.CreateCategory
                    or FeedCatalogBatchOperationType.PatchCategory
                    or FeedCatalogBatchOperationType.DeleteCategory
                    ? "FEED_CATEGORY"
                    : "FEED",
                $"10000000-0000-4000-8000-{index + 100:D12}")).ToArray();
            return Task.FromResult(new FeedCatalogBatchResult(expectedCatalogVersion + 1, results));
        }
    }

    private sealed class FakeOpmlFileService : IOpmlFileService
    {
        public OpmlDocument? Loaded { get; set; }
        public OpmlDocument? Saved { get; private set; }

        public Task<OpmlDocument> LoadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(Loaded ?? throw new InvalidOperationException("Missing OPML fixture."));

        public Task SaveAsync(string path, OpmlDocument document, CancellationToken cancellationToken)
        {
            Saved = document;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOpmlFileDialogs : IOpmlFileDialogService
    {
        public string? ImportPath { get; set; }
        public string? ExportPath { get; set; }
        public string? PickOpmlImport() => ImportPath;
        public string? PickOpmlExport(string suggestedFileName) => ExportPath;
    }

    private sealed class FakeFeedFetchStateRepository : IFeedFetchStateRepository
    {
        public IReadOnlyList<FeedRefreshTarget> Targets { get; set; } = [];

        public Task<FeedRefreshTarget?> GetTargetAsync(
            string feedId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Targets.FirstOrDefault(target => target.Feed.Id == feedId));

        public Task<IReadOnlyList<FeedRefreshTarget>> GetAllTargetsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Targets);

        public Task<IReadOnlyList<FeedRefreshTarget>> GetDueTargetsAsync(
            DateTimeOffset now,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FeedRefreshTarget>>([]);

        public Task<bool> SaveStateAsync(
            FeedFetchState state,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class FakeFeedRefreshService : IFeedRefreshService
    {
        public List<(string FeedId, bool Force)> Calls { get; } = [];
        public Dictionary<string, FeedRefreshResult> Results { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedRefreshResult> RefreshAsync(
            string feedId,
            bool force,
            CancellationToken cancellationToken)
        {
            Calls.Add((feedId, force));
            return Task.FromResult(Results.GetValueOrDefault(feedId)
                ?? new(feedId, FeedRefreshOutcome.Failed, 0, null, "network"));
        }

        public Task<FeedRefreshBatchResult> RefreshDueAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FeedRefreshBatchResult(0, 0, 0, 0));
    }

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
        public AppException? Failure { get; set; }
        public FeedCatalogSyncStatus Current { get; private set; } = new(
            false, 0, FeedCatalogScope.All, null, false, 0, null, null);
        public event EventHandler<FeedCatalogSyncStatusChangedEventArgs>? StatusChanged;
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<FeedCatalogSyncResult> SyncAsync(CancellationToken token)
        {
            SyncCount++;
            if (Failure is not null) return Task.FromException<FeedCatalogSyncResult>(Failure);
            OnSync?.Invoke();
            StatusChanged?.Invoke(this, new(Current));
            return Task.FromResult(new FeedCatalogSyncResult(FeedCatalogSyncOutcome.Updated, 0, Now));
        }
    }

    private sealed class FakeDiscoveryService : IFeedDiscoveryService
    {
        public FeedDiscoveryResult? Result { get; set; }
        public AppException? Failure { get; set; }
        public Task<FeedDiscoveryResult> DiscoverAsync(string url, CancellationToken token) =>
            Failure is null
                ? Task.FromResult(Result ?? throw new InvalidOperationException("Missing discovery result."))
                : Task.FromException<FeedDiscoveryResult>(Failure);
    }

    private sealed class FakeAccountSessionService(AccountSessionSnapshot current) : IAccountSessionService
    {
        public bool IsConfigured => true;
        public AccountSessionSnapshot Current { get; private set; } = current;
        public event EventHandler<AccountSessionChangedEventArgs>? SessionChanged;
        public void SetSession(AccountSessionSnapshot session)
        {
            Current = session;
            SessionChanged?.Invoke(this, new(session));
        }
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task LoginAsync(string username, string password, CancellationToken token) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken token) => Task.CompletedTask;
    }
}
