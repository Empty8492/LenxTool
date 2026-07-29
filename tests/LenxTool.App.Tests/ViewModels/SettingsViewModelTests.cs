using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Core.Updates;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.App.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task SaveSecretsPersistsTrimmedInputsAndReportsConfiguredState()
    {
        var secrets = new FakeSecretStore();
        var viewModel = new SettingsViewModel(
            new FakeThemeService(),
            new FakeUpdateService(),
            secrets,
            new FakeSettingsRepository(),
            new FakeAccountSessionService(),
            new FakeFeedCatalogSyncService());

        Assert.False(viewModel.SaveSecretsCommand.CanExecute(null));

        viewModel.GroqKeyInput = "  gsk_test_not_a_real_key  ";
        viewModel.DeepSeekKeyInput = "  sk-test-not-a-real-key  ";

        Assert.True(viewModel.SaveSecretsCommand.CanExecute(null));
        await viewModel.SaveSecretsCommand.ExecuteAsync();

        Assert.Equal("gsk_test_not_a_real_key", secrets.Values["groq_api_key"]);
        Assert.Equal("sk-test-not-a-real-key", secrets.Values["deepseek_api_key"]);
        Assert.Empty(viewModel.GroqKeyInput);
        Assert.Empty(viewModel.DeepSeekKeyInput);
        Assert.Equal("Groq：已配置 · DeepSeek：已配置 · 已加密保存", viewModel.SecretStatus);
        Assert.False(viewModel.SaveSecretsCommand.CanExecute(null));
    }

    [Fact]
    public async Task InitializeRestoresAppearanceAndSavePersistsChanges()
    {
        var theme = new FakeThemeService();
        var settings = new FakeSettingsRepository
        {
            Values =
            {
                ["appearance.dark_mode"] = "True",
                ["appearance.reduce_motion"] = "True"
            }
        };
        var viewModel = new SettingsViewModel(
            theme,
            new FakeUpdateService(),
            new FakeSecretStore(),
            settings,
            new FakeAccountSessionService(),
            new FakeFeedCatalogSyncService());

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.IsDarkMode);
        Assert.True(viewModel.ReduceMotion);
        Assert.True(theme.DarkMode);
        Assert.True(theme.ReduceMotion);

        viewModel.IsDarkMode = false;
        viewModel.ReduceMotion = false;
        await viewModel.SaveAppearanceCommand.ExecuteAsync();

        Assert.Equal("False", settings.Values["appearance.dark_mode"]);
        Assert.Equal("False", settings.Values["appearance.reduce_motion"]);
        Assert.Contains("已保存", viewModel.AppearanceStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginDisplaysRoleAndQuotaWithoutRetainingPassword()
    {
        var account = new FakeAccountSessionService();
        var viewModel = CreateViewModel(account);
        viewModel.AccountUsernameInput = "owner";

        Assert.False(viewModel.LoginCommand.CanExecute(null));

        viewModel.AccountPasswordInput = "correct horse battery staple";
        Assert.True(viewModel.LoginCommand.CanExecute(null));
        await viewModel.LoginCommand.ExecuteAsync();

        Assert.True(viewModel.IsSignedIn);
        Assert.False(viewModel.IsSignedOut);
        Assert.True(viewModel.IsAdmin);
        Assert.Equal("owner · 管理员", viewModel.AccountIdentity);
        Assert.Equal("AI 88/100 · 语音 3555/3600 秒 · 2026-07-22 UTC", viewModel.AccountQuotaSummary);
        Assert.Empty(viewModel.AccountPasswordInput);
        Assert.False(viewModel.LoginCommand.CanExecute(null));
    }

    [Fact]
    public async Task InitializeShowsExpiredSessionPromptAndLogoutReturnsToSignedOut()
    {
        var account = new FakeAccountSessionService
        {
            InitializeSession = AccountSessionSnapshot.Expired
        };
        var viewModel = CreateViewModel(account);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.IsSignedOut);
        Assert.Contains("已过期", viewModel.AccountStatus, StringComparison.Ordinal);

        account.SetSession(SignedIn(AccountRole.User));
        Assert.False(viewModel.IsAdmin);
        await viewModel.LogoutCommand.ExecuteAsync();

        Assert.True(viewModel.IsSignedOut);
        Assert.Contains("已退出", viewModel.AccountStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingProviderKeysDoesNotRotateAccountSession()
    {
        var account = new FakeAccountSessionService();
        var viewModel = CreateViewModel(account);

        await viewModel.DeleteSecretsCommand.ExecuteAsync();

        Assert.Equal(0, account.InitializeCalls);
    }

    [Fact]
    public async Task LoginStorageFailureIsReportedAndPasswordIsCleared()
    {
        var account = new FakeAccountSessionService { LoginException = new IOException("test failure") };
        var viewModel = CreateViewModel(account);
        viewModel.AccountUsernameInput = "owner";
        viewModel.AccountPasswordInput = "password";

        await viewModel.LoginCommand.ExecuteAsync();

        Assert.True(viewModel.IsSignedOut);
        Assert.Empty(viewModel.AccountPasswordInput);
        Assert.Contains("令牌", viewModel.AccountStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("test failure", viewModel.AccountStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeStartsCatalogSyncAndDisplaysLastSuccessfulStaleState()
    {
        var sync = new FakeFeedCatalogSyncService();
        var viewModel = CreateViewModel(new FakeAccountSessionService(), sync);

        await viewModel.InitializeAsync(CancellationToken.None);
        sync.SetStatus(new(
            false,
            12,
            FeedCatalogScope.Active,
            new DateTimeOffset(2026, 7, 22, 8, 30, 0, TimeSpan.Zero),
            true,
            1,
            new DateTimeOffset(2026, 7, 22, 8, 31, 0, TimeSpan.Zero),
            null));

        Assert.Equal(1, sync.InitializeCalls);
        Assert.Contains("v12", viewModel.CatalogSyncStatus, StringComparison.Ordinal);
        Assert.Contains("已过期", viewModel.CatalogSyncStatus, StringComparison.Ordinal);
        Assert.Contains("本地版本", viewModel.CatalogSyncStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAlsoLoadsOptionalObsidianSettings()
    {
        var obsidianStore = new FakeObsidianExportTargetStore
        {
            Current = new(
                "default",
                @"D:\知识库",
                "Lenx",
                null,
                ["feed"],
                true)
        };
        var obsidian = new ObsidianSettingsViewModel(
            obsidianStore,
            new FakeDesktopFileDialogService());
        var viewModel = CreateViewModel(
            new FakeAccountSessionService(),
            obsidianSettings: obsidian);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Same(obsidian, viewModel.ObsidianSettings);
        Assert.Equal(@"D:\知识库", obsidian.VaultRootPath);
        Assert.Equal("Lenx", obsidian.RelativeDirectory);
    }

    [Fact]
    public async Task StorageCleanupRequiresPreviewThenRefreshesUsage()
    {
        var maintenance = new FakeDatabaseMaintenanceService
        {
            Usage = new(
                10L * 1024 * 1024,
                2L * 1024 * 1024,
                3,
                4L * 1024 * 1024,
                1),
            Preview = new(
                new DateTimeOffset(2026, 1, 28, 0, 0, 0, TimeSpan.Zero),
                12,
                2,
                2048),
            Result = new(
                new DateTimeOffset(2026, 1, 28, 0, 0, 0, TimeSpan.Zero),
                12,
                2,
                2048,
                true,
                new(9L * 1024 * 1024, 1024, 1, 4L * 1024 * 1024, 1))
        };
        var viewModel = CreateViewModel(
            new FakeAccountSessionService(),
            maintenance: maintenance);

        await viewModel.RefreshStorageUsageCommand.ExecuteAsync();

        Assert.Equal("10.0 MB", viewModel.DatabaseUsage);
        Assert.Equal("2.0 MB · 3 个文件", viewModel.ImageCacheUsage);
        Assert.Equal("4.0 MB · 1 个文件", viewModel.ModelUsage);
        Assert.False(viewModel.ConfirmStorageCleanupCommand.CanExecute(null));

        await viewModel.PreviewStorageCleanupCommand.ExecuteAsync();

        Assert.True(viewModel.IsStorageCleanupPreviewVisible);
        Assert.Contains(
            "12 条",
            viewModel.StorageCleanupPreviewSummary,
            StringComparison.Ordinal);
        Assert.Contains(
            "2.0 KB",
            viewModel.StorageCleanupPreviewSummary,
            StringComparison.Ordinal);
        Assert.Equal(0, maintenance.RunCalls);
        Assert.True(viewModel.ConfirmStorageCleanupCommand.CanExecute(null));

        await viewModel.ConfirmStorageCleanupCommand.ExecuteAsync();

        Assert.Equal(1, maintenance.RunCalls);
        Assert.False(viewModel.IsStorageCleanupPreviewVisible);
        Assert.Equal("9.0 MB", viewModel.DatabaseUsage);
        Assert.Contains(
            "已清理 12 条",
            viewModel.StorageStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelingStoragePreviewNeverRunsCleanup()
    {
        var maintenance = new FakeDatabaseMaintenanceService
        {
            Preview = new(
                new DateTimeOffset(2026, 1, 28, 0, 0, 0, TimeSpan.Zero),
                5,
                1,
                1024)
        };
        var viewModel = CreateViewModel(
            new FakeAccountSessionService(),
            maintenance: maintenance);
        await viewModel.PreviewStorageCleanupCommand.ExecuteAsync();

        viewModel.CancelStorageCleanupPreviewCommand.Execute(null);

        Assert.False(viewModel.IsStorageCleanupPreviewVisible);
        Assert.Equal(0, maintenance.RunCalls);
        Assert.False(viewModel.ConfirmStorageCleanupCommand.CanExecute(null));
    }

    private static SettingsViewModel CreateViewModel(
        IAccountSessionService account,
        IFeedCatalogSyncService? sync = null,
        IDatabaseMaintenanceService? maintenance = null,
        ObsidianSettingsViewModel? obsidianSettings = null) => new(
        new FakeThemeService(),
        new FakeUpdateService(),
        new FakeSecretStore(),
        new FakeSettingsRepository(),
        account,
        sync ?? new FakeFeedCatalogSyncService(),
        maintenance,
        obsidianSettings: obsidianSettings);

    private static AccountSessionSnapshot SignedIn(AccountRole role) => new(
        AccountSessionStatus.SignedIn,
        new("10000000-0000-4000-8000-000000000001", "owner", role),
        new(
            new DateOnly(2026, 7, 22),
            new(100, 12, 0, 88),
            new(3600, 45, 0, 3555)));

    private sealed class FakeThemeService : IThemeService
    {
        public bool DarkMode { get; private set; }
        public bool ReduceMotion { get; private set; }
        public void ApplyTheme(bool useDarkTheme) => DarkMode = useDarkTheme;
        public void ApplyReduceMotion(bool reduceMotion) => ReduceMotion = reduceMotion;
    }

    private sealed class FakeSettingsRepository : IAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(name));
        public Task SetAsync(string name, string value, CancellationToken cancellationToken)
        {
            Values[name] = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            Values.Remove(name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public Task<UpdateCandidate?> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult<UpdateCandidate?>(null);
        public Task<string> DownloadAsync(UpdateCandidate candidate, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public void LaunchInstallerAndExit(string installerPath) => throw new NotSupportedException();
    }

    private sealed class FakeAccountSessionService : IAccountSessionService
    {
        public bool IsConfigured { get; set; } = true;
        public AccountSessionSnapshot Current { get; private set; } = AccountSessionSnapshot.SignedOut;
        public AccountSessionSnapshot InitializeSession { get; init; } = AccountSessionSnapshot.SignedOut;
        public int InitializeCalls { get; private set; }
        public Exception? LoginException { get; init; }
        public event EventHandler<AccountSessionChangedEventArgs>? SessionChanged;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCalls++;
            SetSession(InitializeSession);
            return Task.CompletedTask;
        }

        public Task LoginAsync(string username, string password, CancellationToken cancellationToken)
        {
            if (LoginException is not null) throw LoginException;
            SetSession(SignedIn(AccountRole.Admin));
            return Task.CompletedTask;
        }

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LogoutAsync(CancellationToken cancellationToken)
        {
            SetSession(AccountSessionSnapshot.SignedOut);
            return Task.CompletedTask;
        }

        public void SetSession(AccountSessionSnapshot session)
        {
            Current = session;
            SessionChanged?.Invoke(this, new(session));
        }
    }

    private sealed class FakeFeedCatalogSyncService : IFeedCatalogSyncService
    {
        public FeedCatalogSyncStatus Current { get; private set; } = new(
            false,
            0,
            FeedCatalogScope.Active,
            null,
            true,
            0,
            null,
            null);
        public int InitializeCalls { get; private set; }
        public event EventHandler<FeedCatalogSyncStatusChangedEventArgs>? StatusChanged;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCalls++;
            return Task.CompletedTask;
        }

        public Task<FeedCatalogSyncResult> SyncAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FeedCatalogSyncResult(
                FeedCatalogSyncOutcome.SkippedNotAuthenticated,
                Current.Version,
                Current.LastSynchronizedAt));

        public void SetStatus(FeedCatalogSyncStatus status)
        {
            Current = status;
            StatusChanged?.Invoke(this, new(status));
        }
    }

    private sealed class FakeDatabaseMaintenanceService
        : IDatabaseMaintenanceService
    {
        public LocalStorageUsage Usage { get; set; } =
            new(0, 0, 0, 0, 0);
        public StorageCleanupPreview Preview { get; set; } =
            new(DateTimeOffset.UtcNow.AddDays(-180), 0, 0, 0);
        public StorageCleanupResult Result { get; set; } =
            new(
                DateTimeOffset.UtcNow.AddDays(-180),
                0,
                0,
                0,
                true,
                new(0, 0, 0, 0, 0));
        public int RunCalls { get; private set; }

        public Task<string> BackupAsync(
            string? destinationPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(destinationPath ?? "backup.db");

        public Task RestoreAsync(
            string sourcePath,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<LocalStorageUsage> GetStorageUsageAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Usage);

        public Task<StorageCleanupPreview> PreviewCleanupAsync(
            DateTimeOffset cutoff,
            CancellationToken cancellationToken) =>
            Task.FromResult(Preview with { Cutoff = cutoff });

        public Task<StorageCleanupResult> RunCleanupAsync(
            DateTimeOffset cutoff,
            CancellationToken cancellationToken)
        {
            RunCalls++;
            return Task.FromResult(Result with { Cutoff = cutoff });
        }
    }

    private sealed class FakeObsidianExportTargetStore
        : IObsidianExportTargetStore
    {
        public ObsidianExportTarget? Current { get; init; }

        public Task<ObsidianExportTarget?> GetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task SaveAsync(
            ObsidianExportTarget target,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeDesktopFileDialogService
        : IDesktopFileDialogService
    {
        public IReadOnlyList<string> PickMediaFiles() => [];
        public string? PickWhisperModel() => null;
        public string? PickDatabaseBackup() => null;
        public string? PickFileForHash() => null;
        public (string Source, string Destination)? PickWordConversion() =>
            null;
        public string? PickFolder() => null;
        public void OpenFolder(string path)
        {
        }

        public void OpenUri(string uri)
        {
        }
    }
}
