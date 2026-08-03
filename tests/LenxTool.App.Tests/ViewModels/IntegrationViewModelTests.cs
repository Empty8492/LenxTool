using LenxTool.App.ViewModels;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

/// <summary>
/// 冻结管理员 RBAC 与个人凭据只在本机保存的界面行为。
/// </summary>
public sealed class IntegrationViewModelTests
{
    [Fact]
    public void PersonalSettingsOmitsKindsWithDedicatedProviderCards()
    {
        Assert.DoesNotContain(
            IntegrationKindChoice.All,
            item => item.Kind == EntryIntegrationKind.Obsidian);
        Assert.DoesNotContain(
            IntegrationKindChoice.All,
            item => item.Kind == EntryIntegrationKind.Eagle);
        Assert.DoesNotContain(
            IntegrationKindChoice.All,
            item => item.Kind == EntryIntegrationKind.Zotero);
        Assert.Contains(
            IntegrationKindChoice.All,
            item => item.Kind == EntryIntegrationKind.Webhook);
    }

    [Theory]
    [InlineData(EntryIntegrationKind.Obsidian)]
    [InlineData(EntryIntegrationKind.Eagle)]
    public void AdminLocalIntegrationPolicyForcesSharedHostsEmpty(
        EntryIntegrationKind kind)
    {
        var item = new IntegrationPolicyEditorItem(
            kind,
            IntegrationKindChoice.LabelFor(kind),
            isEnabled: true,
            "should-not-be-uploaded.example.com");

        Assert.False(item.RequiresAllowedHosts);
        Assert.Empty(item.AllowedHostsText);
        Assert.Contains("本机", item.HostGuidance, StringComparison.Ordinal);

        item.AllowedHostsText = "still-not-allowed.example.com";
        Assert.Empty(item.AllowedHostsText);
    }

    [Fact]
    public async Task AdminPageNeverReadsAllOrWritesForOrdinaryUser()
    {
        var policies = new FakePolicyService();
        var account = new FakeAccountSession(AccountRole.User);
        var viewModel = new IntegrationAdminViewModel(
            policies,
            account);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.IsAdmin);
        Assert.Equal(0, policies.GetCount);
        Assert.False(viewModel.PublishCommand.CanExecute(null));
        Assert.Empty(viewModel.Policies);
    }

    [Fact]
    public async Task AdminPagePublishesNormalizedWholePolicySet()
    {
        var policies = new FakePolicyService
        {
            Snapshot = new(
                3,
                [
                    new(
                        EntryIntegrationKind.Webhook,
                        false,
                        ["hooks.example.com"])
                ],
                EntryIntegrationPolicyScope.All)
        };
        var viewModel = new IntegrationAdminViewModel(
            policies,
            new FakeAccountSession(AccountRole.Admin));
        await viewModel.InitializeAsync(CancellationToken.None);
        IntegrationPolicyEditorItem webhook =
            viewModel.Policies.Single(
                item => item.Kind == EntryIntegrationKind.Webhook);
        webhook.IsEnabled = true;
        webhook.AllowedHostsText =
            "HOOKS.EXAMPLE.COM.\r\nbackup.example.com";

        await viewModel.PublishCommand.ExecuteAsync();

        Assert.Equal(1, policies.ReplaceCount);
        Assert.Equal(3, policies.ExpectedVersion);
        EntryIntegrationPolicyInput published =
            policies.Inputs.Single(
                input => input.Kind == EntryIntegrationKind.Webhook);
        Assert.True(published.IsEnabled);
        Assert.Equal(
            ["backup.example.com", "hooks.example.com"],
            published.AllowedHosts);
    }

    [Fact]
    public async Task PersonalSettingsStoresOnlySecretInCredentialStoreAndClearsInput()
    {
        var credentials = new FakeCredentialStore();
        var localSettings = new FakeSettingsRepository();
        var health = new FakeHealthService();
        var viewModel = new IntegrationSettingsViewModel(
            credentials,
            health,
            localSettings)
        {
            SelectedKind = IntegrationKindChoice.All.Single(
                item => item.Kind == EntryIntegrationKind.Readwise),
            TargetId = "personal",
            EndpointText = "https://api.readwise.io/v2/",
            CredentialInput = "private-token"
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal("private-token", credentials.Value);
        Assert.Empty(viewModel.CredentialInput);
        Assert.Equal(
            "personal",
            localSettings.Values["integration.target.id"]);
        Assert.Equal(
            "https://api.readwise.io/v2/",
            localSettings.Values["integration.target.endpoint"]);
        Assert.DoesNotContain(
            localSettings.Values.Values,
            value => value.Contains(
                "private-token",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task PersonalCredentialCanBeDeletedAfterEndpointIsCleared()
    {
        var credentials = new FakeCredentialStore();
        var viewModel = new IntegrationSettingsViewModel(
            credentials,
            new FakeHealthService(),
            new FakeSettingsRepository())
        {
            TargetId = "personal",
            EndpointText = "https://hooks.example.com/health",
            CredentialInput = "private-token"
        };
        await viewModel.SaveCommand.ExecuteAsync();

        viewModel.EndpointText = string.Empty;
        Assert.True(viewModel.DeleteCredentialCommand.CanExecute(null));
        await viewModel.DeleteCredentialCommand.ExecuteAsync();

        Assert.Null(credentials.Value);
        Assert.False(viewModel.HasCredential);
    }

    [Fact]
    public async Task PersonalSettingsDisplaysOnlyClosedHealthStatus()
    {
        var health = new FakeHealthService
        {
            Result = new(
                EntryIntegrationHealthStatus.AdapterUnavailable,
                DateTimeOffset.UtcNow)
        };
        var viewModel = new IntegrationSettingsViewModel(
            new FakeCredentialStore(),
            health,
            new FakeSettingsRepository())
        {
            TargetId = "personal",
            EndpointText = "https://hooks.example.com/health"
        };

        await viewModel.TestCommand.ExecuteAsync();

        Assert.Contains("尚未安装", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(1, health.Count);
    }

    private sealed class FakePolicyService
        : IEntryIntegrationPolicyService
    {
        public EntryIntegrationPolicySnapshot Snapshot { get; set; } =
            new(
                0,
                [],
                EntryIntegrationPolicyScope.All);
        public int GetCount { get; private set; }
        public int ReplaceCount { get; private set; }
        public long ExpectedVersion { get; private set; }
        public IReadOnlyList<EntryIntegrationPolicyInput> Inputs
        {
            get;
            private set;
        } = [];

        public Task<EntryIntegrationPolicySnapshot> GetAsync(
            EntryIntegrationPolicyScope scope,
            CancellationToken cancellationToken)
        {
            GetCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
            IReadOnlyList<EntryIntegrationPolicyInput> inputs,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            ReplaceCount++;
            Inputs = inputs;
            ExpectedVersion = expectedVersion;
            IReadOnlyList<EntryIntegrationPolicy> normalized =
                LenxTool.Core.Exports.EntryIntegrationPolicyValidator
                    .ValidateAndNormalizeSet(inputs);
            Snapshot = new(
                expectedVersion + 1,
                normalized,
                EntryIntegrationPolicyScope.All);
            return Task.FromResult(
                new EntryIntegrationPolicyMutationResult(
                    Snapshot.Version,
                    normalized,
                    IsReplay: false));
        }
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

        private static AccountSessionSnapshot SignedIn(
            AccountRole value) => new(
            AccountSessionStatus.SignedIn,
            new("account-id", "owner", value));
    }

    private sealed class FakeCredentialStore
        : IEntryIntegrationCredentialStore
    {
        public string? Value { get; private set; }

        public Task<string?> GetAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Value);
        public Task<bool> ExistsAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Value is not null);
        public Task SetAsync(
            EntryIntegrationKind kind,
            string targetId,
            string value,
            CancellationToken cancellationToken)
        {
            Value = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            Value = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHealthService
        : IEntryIntegrationHealthService
    {
        public EntryIntegrationHealthResult Result { get; set; } =
            new(
                EntryIntegrationHealthStatus.Healthy,
                DateTimeOffset.UtcNow);
        public int Count { get; private set; }

        public Task<EntryIntegrationHealthResult> CheckAsync(
            EntryIntegrationTarget target,
            CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeSettingsRepository
        : IAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<string?> GetAsync(
            string key,
            CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }
}
