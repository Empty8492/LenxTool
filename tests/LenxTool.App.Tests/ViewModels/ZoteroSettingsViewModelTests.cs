using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.App.Tests.ViewModels;

/// <summary>
/// 冻结 Zotero 个人库设置：非敏感目标进入版本化设置，API key 只进入 DPAPI 槽位。
/// </summary>
public sealed class ZoteroSettingsViewModelTests
{
    [Fact]
    public void ConstructorDefaultsAttachmentUploadOffAndOffersOnlySupportedTypes()
    {
        ZoteroSettingsViewModel viewModel = CreateViewModel(
            new FakeTargetStore(),
            new FakeCredentialStore());

        Assert.False(viewModel.UploadFirstImageAttachment);
        Assert.Equal(
            [ZoteroItemType.Webpage, ZoteroItemType.JournalArticle],
            viewModel.ItemTypes.Select(item => item.Value));
    }

    [Fact]
    public async Task InitializeRestoresTargetAndOnlyReadsCredentialPresence()
    {
        var targetStore = new FakeTargetStore
        {
            Current = new(
                "default",
                99887766,
                ZoteroItemType.JournalArticle,
                IncludeSummaryNote: false,
                UploadFirstImageAttachment: true)
        };
        var credentials = new FakeCredentialStore
        {
            Value = "private-api-key"
        };
        var viewModel = CreateViewModel(targetStore, credentials);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal("99887766", viewModel.UserIdText);
        Assert.Equal(
            ZoteroItemType.JournalArticle,
            viewModel.SelectedItemType.Value);
        Assert.False(viewModel.IncludeSummaryNote);
        Assert.True(viewModel.UploadFirstImageAttachment);
        Assert.True(viewModel.HasCredential);
        Assert.Empty(viewModel.CredentialInput);
        Assert.Equal(0, credentials.GetCalls);
        Assert.Equal(1, credentials.ExistsCalls);
    }

    [Fact]
    public async Task SavePersistsNormalizedTargetAndCredentialWithoutNetwork()
    {
        var targetStore = new FakeTargetStore();
        var credentials = new FakeCredentialStore();
        var health = new FakeHealthService();
        var viewModel = CreateViewModel(targetStore, credentials, health);
        viewModel.UserIdText = " 12345678 ";
        viewModel.SelectedItemType = viewModel.ItemTypes.Single(
            item => item.Value == ZoteroItemType.JournalArticle);
        viewModel.IncludeSummaryNote = true;
        viewModel.UploadFirstImageAttachment = false;
        viewModel.CredentialInput = " private-api-key ";

        await viewModel.SaveCommand.ExecuteAsync();

        ZoteroExportTarget saved = Assert.IsType<ZoteroExportTarget>(
            targetStore.Saved);
        Assert.Equal("default", saved.TargetId);
        Assert.Equal(12345678, saved.UserId);
        Assert.Equal(ZoteroItemType.JournalArticle, saved.ItemType);
        Assert.True(saved.IncludeSummaryNote);
        Assert.False(saved.UploadFirstImageAttachment);
        Assert.Equal("private-api-key", credentials.Value);
        Assert.Equal(
            (EntryIntegrationKind.Zotero, "default"),
            credentials.LastSlot);
        Assert.Empty(viewModel.CredentialInput);
        Assert.True(viewModel.HasCredential);
        Assert.Equal(0, health.Count);
    }

    [Fact]
    public async Task SaveWithoutNewKeyPreservesExistingCredential()
    {
        var credentials = new FakeCredentialStore
        {
            Value = "existing-api-key"
        };
        var viewModel = CreateViewModel(
            new FakeTargetStore(),
            credentials);
        viewModel.UserIdText = "12345678";

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal("existing-api-key", credentials.Value);
        Assert.Equal(0, credentials.SetCalls);
        Assert.True(viewModel.HasCredential);
    }

    [Fact]
    public async Task TestRequiresCurrentFormToMatchSavedTarget()
    {
        var targetStore = new FakeTargetStore
        {
            Current = new(
                ZoteroExportTarget.DefaultTargetId,
                24680,
                ZoteroItemType.Webpage,
                IncludeSummaryNote: false,
                UploadFirstImageAttachment: false)
        };
        var health = new FakeHealthService
        {
            Result = new(
                EntryIntegrationHealthStatus.Healthy,
                DateTimeOffset.UtcNow)
        };
        var viewModel = CreateViewModel(
            targetStore,
            new FakeCredentialStore(),
            health);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.TestCommand.ExecuteAsync();

        EntryIntegrationTarget target = Assert.IsType<EntryIntegrationTarget>(
            health.Target);
        Assert.Equal("default", target.TargetId);
        Assert.Equal(EntryIntegrationKind.Zotero, target.Kind);
        Assert.Equal(
            "https://api.zotero.org/users/24680/",
            target.Endpoint.AbsoluteUri);
        Assert.Equal(0, targetStore.SaveCalls);
        Assert.Contains("通过", viewModel.Status, StringComparison.Ordinal);

        viewModel.UserIdText = "13579";
        await viewModel.TestCommand.ExecuteAsync();

        Assert.Equal(1, health.Count);
        Assert.Equal(0, targetStore.SaveCalls);
        Assert.Contains("先保存", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public async Task SaveRejectsInvalidUserIdAndClearsCredentialInput(
        string userId)
    {
        var targetStore = new FakeTargetStore();
        var credentials = new FakeCredentialStore();
        var viewModel = CreateViewModel(targetStore, credentials);
        viewModel.UserIdText = userId;
        viewModel.CredentialInput = "private-api-key";

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal(0, targetStore.SaveCalls);
        Assert.Equal(0, credentials.SetCalls);
        Assert.Empty(viewModel.CredentialInput);
        Assert.Contains("正整数", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheDefaultZoteroCredential()
    {
        var credentials = new FakeCredentialStore
        {
            Value = "private-api-key"
        };
        var viewModel = CreateViewModel(
            new FakeTargetStore(),
            credentials);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.DeleteCredentialCommand.ExecuteAsync();

        Assert.Null(credentials.Value);
        Assert.Equal(
            (EntryIntegrationKind.Zotero, "default"),
            credentials.LastSlot);
        Assert.False(viewModel.HasCredential);
    }

    private static ZoteroSettingsViewModel CreateViewModel(
        FakeTargetStore targetStore,
        FakeCredentialStore credentials,
        FakeHealthService? health = null) => new(
        targetStore,
        credentials,
        health ?? new FakeHealthService());

    private sealed class FakeTargetStore : IZoteroExportTargetStore
    {
        public ZoteroExportTarget? Current { get; set; }
        public ZoteroExportTarget? Saved { get; private set; }
        public int SaveCalls { get; private set; }

        public Task<ZoteroExportTarget?> GetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task<IZoteroExportTargetLease> AcquireExportLeaseAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IZoteroExportTargetLease>(
                new FakeLease(Current));

        public Task SaveAsync(
            ZoteroExportTarget target,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            Saved = target;
            Current = target;
            return Task.CompletedTask;
        }

        private sealed class FakeLease(ZoteroExportTarget? target)
            : IZoteroExportTargetLease
        {
            public ZoteroExportTarget? Target { get; } = target;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCredentialStore
        : IEntryIntegrationCredentialStore
    {
        public string? Value { get; set; }
        public int GetCalls { get; private set; }
        public int ExistsCalls { get; private set; }
        public int SetCalls { get; private set; }
        public (EntryIntegrationKind Kind, string TargetId)? LastSlot
        {
            get;
            private set;
        }

        public Task<string?> GetAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            LastSlot = (kind, targetId);
            return Task.FromResult(Value);
        }

        public Task<bool> ExistsAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            ExistsCalls++;
            LastSlot = (kind, targetId);
            return Task.FromResult(Value is not null);
        }

        public Task SetAsync(
            EntryIntegrationKind kind,
            string targetId,
            string value,
            CancellationToken cancellationToken)
        {
            SetCalls++;
            LastSlot = (kind, targetId);
            Value = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            LastSlot = (kind, targetId);
            Value = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHealthService
        : IEntryIntegrationHealthService
    {
        public EntryIntegrationHealthResult Result { get; init; } = new(
            EntryIntegrationHealthStatus.AdapterUnavailable,
            DateTimeOffset.UtcNow);
        public EntryIntegrationTarget? Target { get; private set; }
        public int Count { get; private set; }

        public Task<EntryIntegrationHealthResult> CheckAsync(
            EntryIntegrationTarget target,
            CancellationToken cancellationToken)
        {
            Count++;
            Target = target;
            return Task.FromResult(Result);
        }
    }
}
