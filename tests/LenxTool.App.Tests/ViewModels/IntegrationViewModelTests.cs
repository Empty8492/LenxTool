using LenxTool.App.ViewModels;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.App.Tests.ViewModels;

/// <summary>
/// 冻结管理员 RBAC 与个人凭据只在本机保存的界面行为。
/// </summary>
public sealed class IntegrationViewModelTests
{
    [Fact]
    public void PersonalSettingsExposesOnlyWiredGenericAdapters()
    {
        IntegrationKindChoice choice = Assert.Single(
            IntegrationKindChoice.All);

        Assert.Equal(EntryIntegrationKind.Readwise, choice.Kind);
    }

    [Theory]
    [InlineData(EntryIntegrationKind.Readeck)]
    [InlineData(EntryIntegrationKind.Outline)]
    [InlineData(EntryIntegrationKind.QBittorrent)]
    [InlineData(EntryIntegrationKind.Webhook)]
    public void PersonalSettingsRejectsUnwiredKindAssignment(
        EntryIntegrationKind kind)
    {
        var viewModel = new IntegrationSettingsViewModel(
            new FakeCredentialStore(),
            new FakeHealthService(),
            new FakeSettingsRepository())
        {
            TargetId = "untrusted-target",
            EndpointText = "https://unwired.example.com/"
        };

        viewModel.SelectedKind = new(
            kind,
            IntegrationKindChoice.LabelFor(kind));

        Assert.Equal(
            EntryIntegrationKind.Readwise,
            viewModel.SelectedKind.Kind);
        Assert.Equal(
            ReadwiseEntryExporter.CredentialTargetId,
            viewModel.TargetId);
        Assert.Equal(
            ReadwiseEntryExporter.ApiRoot.AbsoluteUri,
            viewModel.EndpointText);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.DeleteCredentialCommand.CanExecute(null));
        Assert.False(viewModel.TestCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(EntryIntegrationKind.Readeck)]
    [InlineData(EntryIntegrationKind.Outline)]
    [InlineData(EntryIntegrationKind.QBittorrent)]
    [InlineData(EntryIntegrationKind.Webhook)]
    public async Task PersonalSettingsIgnoresPersistedUnwiredKind(
        EntryIntegrationKind kind)
    {
        var localSettings = new FakeSettingsRepository();
        localSettings.Values["integration.target.kind"] = kind.ToString();
        localSettings.Values["integration.target.id"] = "legacy-target";
        localSettings.Values["integration.target.endpoint"] =
            "https://legacy.example.com/";
        var viewModel = new IntegrationSettingsViewModel(
            new FakeCredentialStore(),
            new FakeHealthService(),
            localSettings);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(
            EntryIntegrationKind.Readwise,
            viewModel.SelectedKind.Kind);
        Assert.Equal(
            ReadwiseEntryExporter.CredentialTargetId,
            viewModel.TargetId);
        Assert.Equal(
            ReadwiseEntryExporter.ApiRoot.AbsoluteUri,
            viewModel.EndpointText);
    }

    [Fact]
    public async Task LegacyUnwiredCredentialRemainsExplicitlyDeletableAfterReadwiseSave()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(
            EntryIntegrationKind.Readeck,
            "legacy-target",
            "legacy-token");
        var localSettings = new FakeSettingsRepository();
        localSettings.Values["integration.target.kind"] = "Readeck";
        localSettings.Values["integration.target.id"] = "legacy-target";
        localSettings.Values["integration.target.endpoint"] =
            "https://legacy.example.com/";
        var health = new FakeHealthService();
        var viewModel = new IntegrationSettingsViewModel(
            credentials,
            health,
            localSettings);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.HasLegacyCredential);
        Assert.Contains(
            "Readeck",
            viewModel.LegacyCredentialStatus,
            StringComparison.Ordinal);
        Assert.True(viewModel.DeleteLegacyCredentialCommand.CanExecute(null));
        Assert.Equal(0, health.Count);
        Assert.DoesNotContain(
            (EntryIntegrationKind.Readeck, "legacy-target"),
            credentials.ExistsSlots);
        Assert.DoesNotContain(
            (EntryIntegrationKind.Readeck, "legacy-target"),
            credentials.GetSlots);
        Assert.Contains(
            (
                EntryIntegrationKind.Readwise,
                ReadwiseEntryExporter.CredentialTargetId),
            credentials.ExistsSlots);
        Assert.Equal(
            "Readeck",
            localSettings.Values["integration.legacy.kind"]);
        Assert.Equal(
            "legacy-target",
            localSettings.Values["integration.legacy.target.id"]);

        viewModel.CredentialInput = "reader-token";
        await viewModel.SaveCommand.ExecuteAsync();
        var reloaded = new IntegrationSettingsViewModel(
            credentials,
            health,
            localSettings);
        await reloaded.InitializeAsync(CancellationToken.None);

        Assert.True(reloaded.HasLegacyCredential);
        int getCountBeforeDelete = credentials.GetSlots.Count;
        int existsCountBeforeDelete = credentials.ExistsSlots.Count;
        int setCountBeforeDelete = credentials.SetSlots.Count;
        int deleteCountBeforeDelete = credentials.DeleteSlots.Count;
        await reloaded.DeleteLegacyCredentialCommand.ExecuteAsync();

        Assert.Equal(getCountBeforeDelete, credentials.GetSlots.Count);
        Assert.Equal(existsCountBeforeDelete, credentials.ExistsSlots.Count);
        Assert.Equal(setCountBeforeDelete, credentials.SetSlots.Count);
        Assert.Equal(
            deleteCountBeforeDelete + 1,
            credentials.DeleteSlots.Count);
        Assert.Equal(
            (EntryIntegrationKind.Readeck, "legacy-target"),
            credentials.DeleteSlots[^1]);

        Assert.False(await credentials.ExistsAsync(
            EntryIntegrationKind.Readeck,
            "legacy-target",
            CancellationToken.None));
        Assert.True(await credentials.ExistsAsync(
            EntryIntegrationKind.Readwise,
            ReadwiseEntryExporter.CredentialTargetId,
            CancellationToken.None));
        Assert.False(reloaded.HasLegacyCredential);
        Assert.Empty(localSettings.Values["integration.legacy.kind"]);
        Assert.Empty(localSettings.Values["integration.legacy.target.id"]);
    }

    [Fact]
    public async Task DirectLegacyCredentialDeletionDoesNotRecreateCleanupMarker()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(
            EntryIntegrationKind.Readeck,
            "legacy-target",
            "legacy-token");
        var localSettings = new FakeSettingsRepository();
        localSettings.Values["integration.target.kind"] = "Readeck";
        localSettings.Values["integration.target.id"] = "legacy-target";
        localSettings.Values["integration.target.endpoint"] =
            "https://legacy.example.com/";
        var viewModel = new IntegrationSettingsViewModel(
            credentials,
            new FakeHealthService(),
            localSettings);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.DeleteLegacyCredentialCommand.ExecuteAsync();
        var reloaded = new IntegrationSettingsViewModel(
            credentials,
            new FakeHealthService(),
            localSettings);
        await reloaded.InitializeAsync(CancellationToken.None);

        Assert.False(reloaded.HasLegacyCredential);
        Assert.Equal(
            EntryIntegrationKind.Readwise.ToString(),
            localSettings.Values["integration.target.kind"]);
        Assert.Equal(
            ReadwiseEntryExporter.CredentialTargetId,
            localSettings.Values["integration.target.id"]);
        Assert.Equal(
            ReadwiseEntryExporter.ApiRoot.AbsoluteUri,
            localSettings.Values["integration.target.endpoint"]);
        Assert.Empty(localSettings.Values["integration.legacy.kind"]);
        Assert.Empty(localSettings.Values["integration.legacy.target.id"]);
    }

    [Fact]
    public async Task LegacyDeletionDoesNotOverwriteNewerUnwiredTargetSettings()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(
            EntryIntegrationKind.Readeck,
            "legacy-target",
            "legacy-token");
        var localSettings = new FakeSettingsRepository();
        localSettings.Values["integration.target.kind"] = "Readeck";
        localSettings.Values["integration.target.id"] = "legacy-target";
        localSettings.Values["integration.target.endpoint"] =
            "https://legacy.example.com/";
        var viewModel = new IntegrationSettingsViewModel(
            credentials,
            new FakeHealthService(),
            localSettings);
        await viewModel.InitializeAsync(CancellationToken.None);

        localSettings.Values["integration.target.kind"] = "Webhook";
        localSettings.Values["integration.target.id"] = "newer-target";
        localSettings.Values["integration.target.endpoint"] =
            "https://newer.example.com/";
        await viewModel.DeleteLegacyCredentialCommand.ExecuteAsync();
        var reloaded = new IntegrationSettingsViewModel(
            credentials,
            new FakeHealthService(),
            localSettings);
        await reloaded.InitializeAsync(CancellationToken.None);

        Assert.Equal(
            EntryIntegrationKind.Webhook.ToString(),
            localSettings.Values["integration.target.kind"]);
        Assert.Equal(
            "newer-target",
            localSettings.Values["integration.target.id"]);
        Assert.Equal(
            "https://newer.example.com/",
            localSettings.Values["integration.target.endpoint"]);
        Assert.True(reloaded.HasLegacyCredential);
        Assert.Contains(
            "Webhook",
            reloaded.LegacyCredentialStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialLegacyNormalizationRemainsSafelyRetryable()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(
            EntryIntegrationKind.Readeck,
            "legacy-target",
            "legacy-token");
        var localSettings = new FakeSettingsRepository();
        localSettings.Values["integration.target.kind"] = "Readeck";
        localSettings.Values["integration.target.id"] = "legacy-target";
        localSettings.Values["integration.target.endpoint"] =
            "https://legacy.example.com/";
        var viewModel = new IntegrationSettingsViewModel(
            credentials,
            new FakeHealthService(),
            localSettings);
        await viewModel.InitializeAsync(CancellationToken.None);
        localSettings.FailOnSetCall = localSettings.SetCount + 2;

        await Assert.ThrowsAsync<IOException>(() =>
            viewModel.DeleteLegacyCredentialCommand.ExecuteAsync());

        Assert.Equal(
            EntryIntegrationKind.Readwise.ToString(),
            localSettings.Values["integration.target.kind"]);
        Assert.Equal(
            "Readeck",
            localSettings.Values["integration.legacy.kind"]);
        localSettings.FailOnSetCall = null;
        var retry = new IntegrationSettingsViewModel(
            credentials,
            new FakeHealthService(),
            localSettings);
        await retry.InitializeAsync(CancellationToken.None);
        Assert.True(retry.HasLegacyCredential);

        await retry.DeleteLegacyCredentialCommand.ExecuteAsync();
        var reloaded = new IntegrationSettingsViewModel(
            credentials,
            new FakeHealthService(),
            localSettings);
        await reloaded.InitializeAsync(CancellationToken.None);

        Assert.False(reloaded.HasLegacyCredential);
        Assert.Equal(
            EntryIntegrationKind.Readwise.ToString(),
            localSettings.Values["integration.target.kind"]);
    }

    [Fact]
    public async Task ManualLegacyCleanupDeletesAnOlderUnreferencedSlotOnly()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(
            EntryIntegrationKind.Readeck,
            "older-target",
            "older-token");
        credentials.Seed(
            EntryIntegrationKind.Webhook,
            "current-target",
            "current-token");
        var localSettings = new FakeSettingsRepository();
        localSettings.Values["integration.target.kind"] = "Webhook";
        localSettings.Values["integration.target.id"] = "current-target";
        localSettings.Values["integration.target.endpoint"] =
            "https://current.example.com/";
        var health = new FakeHealthService();
        var viewModel = new IntegrationSettingsViewModel(
            credentials,
            health,
            localSettings);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedLegacyCleanupKind =
            IntegrationKindChoice.LegacyCleanupKinds.Single(
                item => item.Kind == EntryIntegrationKind.Readeck);
        viewModel.LegacyCleanupTargetId = "older-target";
        Assert.True(
            viewModel.DeleteSpecifiedLegacyCredentialCommand
                .CanExecute(null));
        int settingsSetCountBeforeDelete = localSettings.SetCount;
        int credentialGetCountBeforeDelete = credentials.GetSlots.Count;
        int credentialExistsCountBeforeDelete =
            credentials.ExistsSlots.Count;
        int credentialSetCountBeforeDelete = credentials.SetSlots.Count;
        int credentialDeleteCountBeforeDelete =
            credentials.DeleteSlots.Count;
        await viewModel.DeleteSpecifiedLegacyCredentialCommand
            .ExecuteAsync();

        Assert.Equal(
            settingsSetCountBeforeDelete,
            localSettings.SetCount);
        Assert.Equal(
            credentialGetCountBeforeDelete,
            credentials.GetSlots.Count);
        Assert.Equal(
            credentialExistsCountBeforeDelete,
            credentials.ExistsSlots.Count);
        Assert.Equal(
            credentialSetCountBeforeDelete,
            credentials.SetSlots.Count);
        Assert.Equal(
            credentialDeleteCountBeforeDelete + 1,
            credentials.DeleteSlots.Count);
        Assert.Equal(
            (EntryIntegrationKind.Readeck, "older-target"),
            credentials.DeleteSlots[^1]);

        Assert.False(await credentials.ExistsAsync(
            EntryIntegrationKind.Readeck,
            "older-target",
            CancellationToken.None));
        Assert.True(await credentials.ExistsAsync(
            EntryIntegrationKind.Webhook,
            "current-target",
            CancellationToken.None));
        Assert.Equal(
            EntryIntegrationKind.Webhook.ToString(),
            localSettings.Values["integration.target.kind"]);
        Assert.Equal(
            "current-target",
            localSettings.Values["integration.target.id"]);
        Assert.True(viewModel.HasLegacyCredential);
        Assert.Equal(0, health.Count);
        Assert.Empty(viewModel.LegacyCleanupTargetId);
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
            CredentialInput = "private-token"
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal("private-token", credentials.Value);
        Assert.Empty(viewModel.CredentialInput);
        Assert.Equal(
            ReadwiseEntryExporter.CredentialTargetId,
            localSettings.Values["integration.target.id"]);
        Assert.Equal(
            ReadwiseEntryExporter.ApiRoot.AbsoluteUri,
            localSettings.Values["integration.target.endpoint"]);
        Assert.DoesNotContain(
            localSettings.Values.Values,
            value => value.Contains(
                "private-token",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadwiseSettingsPinsOfficialTargetAndDefaultCredentialSlot()
    {
        var credentials = new FakeCredentialStore();
        var localSettings = new FakeSettingsRepository();
        var viewModel = new IntegrationSettingsViewModel(
            credentials,
            new FakeHealthService(),
            localSettings)
        {
            SelectedKind = IntegrationKindChoice.All.Single(
                item => item.Kind == EntryIntegrationKind.Readwise),
            CredentialInput = "reader-token"
        };

        Assert.True(viewModel.IsFixedReadwiseTarget);
        Assert.Equal(
            ReadwiseEntryExporter.CredentialTargetId,
            viewModel.TargetId);
        Assert.Equal(
            ReadwiseEntryExporter.ApiRoot.AbsoluteUri,
            viewModel.EndpointText);

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal(EntryIntegrationKind.Readwise, credentials.LastKind);
        Assert.Equal(
            ReadwiseEntryExporter.CredentialTargetId,
            credentials.LastTargetId);
        Assert.Equal("reader-token", credentials.Value);
        Assert.Equal(
            ReadwiseEntryExporter.ApiRoot.AbsoluteUri,
            localSettings.Values["integration.target.endpoint"]);

        viewModel.EndpointText = "https://reader.example.com/";
        viewModel.CredentialInput = "must-not-replace";
        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal("reader-token", credentials.Value);
        Assert.Contains("readwise.io", viewModel.Status, StringComparison.Ordinal);
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
            new FakeSettingsRepository());

        await viewModel.SaveCommand.ExecuteAsync();

        await viewModel.TestCommand.ExecuteAsync();

        Assert.Contains("尚未安装", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(1, health.Count);
    }

    [Fact]
    public async Task PersonalConnectionTestRejectsUnsavedTarget()
    {
        var health = new FakeHealthService();
        var viewModel = new IntegrationSettingsViewModel(
            new FakeCredentialStore(),
            health,
            new FakeSettingsRepository());

        await viewModel.TestCommand.ExecuteAsync();

        Assert.Equal(0, health.Count);
        Assert.Contains("先保存", viewModel.Status, StringComparison.Ordinal);
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
        private readonly Dictionary<
            (EntryIntegrationKind Kind, string TargetId),
            string> _values = [];

        public string? Value =>
            LastKind is { } kind && LastTargetId is { } targetId
                ? _values.GetValueOrDefault((kind, targetId))
                : null;
        public EntryIntegrationKind? LastKind { get; private set; }
        public string? LastTargetId { get; private set; }
        public List<(EntryIntegrationKind Kind, string TargetId)>
            ExistsSlots
        { get; } = [];
        public List<(EntryIntegrationKind Kind, string TargetId)>
            GetSlots
        { get; } = [];
        public List<(EntryIntegrationKind Kind, string TargetId)>
            SetSlots
        { get; } = [];
        public List<(EntryIntegrationKind Kind, string TargetId)>
            DeleteSlots
        { get; } = [];

        public void Seed(
            EntryIntegrationKind kind,
            string targetId,
            string value) =>
            _values[(kind, targetId)] = value;

        public Task<string?> GetAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            GetSlots.Add((kind, targetId));
            return Task.FromResult(
                _values.GetValueOrDefault((kind, targetId)));
        }
        public Task<bool> ExistsAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            ExistsSlots.Add((kind, targetId));
            return Task.FromResult(_values.ContainsKey((kind, targetId)));
        }
        public Task SetAsync(
            EntryIntegrationKind kind,
            string targetId,
            string value,
            CancellationToken cancellationToken)
        {
            LastKind = kind;
            LastTargetId = targetId;
            SetSlots.Add((kind, targetId));
            _values[(kind, targetId)] = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            LastKind = kind;
            LastTargetId = targetId;
            DeleteSlots.Add((kind, targetId));
            _values.Remove((kind, targetId));
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
        public EntryIntegrationTarget? LastTarget { get; private set; }

        public Task<EntryIntegrationHealthResult> CheckAsync(
            EntryIntegrationTarget target,
            CancellationToken cancellationToken)
        {
            Count++;
            LastTarget = target;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeSettingsRepository
        : IAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];
        public int SetCount { get; private set; }
        public int? FailOnSetCall { get; set; }

        public Task<string?> GetAsync(
            string key,
            CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken)
        {
            SetCount++;
            if (SetCount == FailOnSetCall)
            {
                throw new IOException("Injected settings write failure.");
            }
            Values[key] = value;
            return Task.CompletedTask;
        }
    }
}
