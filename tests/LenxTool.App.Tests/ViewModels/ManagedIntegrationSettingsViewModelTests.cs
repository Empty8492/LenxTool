using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.App.Tests.ViewModels;

public sealed class ManagedIntegrationSettingsViewModelTests
{
    private static readonly Uri Endpoint =
        new("https://integration.example.com/");

    [Fact]
    public async Task LegacyCredentialWithoutVersionMarkerStaysInactive()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(EntryIntegrationKind.Readeck, "legacy-token");
        var health = new FakeHealthService();
        var readeck = new FakeTargetStore<ReadeckExportTarget>(
            new("default", Endpoint, Archive: false, CredentialVersion: 0));
        ManagedIntegrationSettingsViewModel viewModel = Create(
            readeck,
            credentials,
            health);

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.TestReadeckCommand.ExecuteAsync();

        Assert.False(viewModel.ReadeckHasCredential);
        Assert.Equal(0, credentials.ExistsCount);
        Assert.Equal(0, health.CheckCount);
        Assert.Equal(0, readeck.Target!.CredentialVersion);
    }

    [Fact]
    public async Task ExplicitCredentialSaveActivatesVersionedTargetAndClearsInput()
    {
        var credentials = new FakeCredentialStore();
        var readeck = new FakeTargetStore<ReadeckExportTarget>(
            new("default", Endpoint, Archive: false, CredentialVersion: 0));
        ManagedIntegrationSettingsViewModel viewModel = Create(
            readeck,
            credentials,
            new FakeHealthService());
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.ReadeckCredential = "new-token";
        viewModel.ReadeckArchive = true;

        await viewModel.SaveReadeckCommand.ExecuteAsync();

        Assert.Equal("new-token", credentials.Get(EntryIntegrationKind.Readeck));
        Assert.Equal(1, readeck.Target!.CredentialVersion);
        Assert.Equal(2, readeck.SavedTargets.Count);
        Assert.Equal(0, readeck.SavedTargets[0].CredentialVersion);
        Assert.Equal(1, readeck.SavedTargets[1].CredentialVersion);
        Assert.True(readeck.Target.Archive);
        Assert.True(viewModel.ReadeckHasCredential);
        Assert.Equal(string.Empty, viewModel.ReadeckCredential);
    }

    [Fact]
    public async Task UnsignedWebhookCanProbeWithoutReadingCredential()
    {
        var credentials = new FakeCredentialStore();
        var health = new FakeHealthService();
        var webhook = new FakeTargetStore<WebhookExportTarget>(
            new("default", new Uri(Endpoint, "hooks/lenxtool"),
                UseHmac: false, CredentialVersion: 0));
        ManagedIntegrationSettingsViewModel viewModel = Create(
            new FakeTargetStore<ReadeckExportTarget>(),
            credentials,
            health,
            webhook);

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.TestWebhookCommand.ExecuteAsync();

        Assert.Equal(1, health.CheckCount);
        Assert.Equal(EntryIntegrationKind.Webhook, health.LastTarget!.Kind);
        Assert.Equal(0, credentials.ExistsCount);
        Assert.False(viewModel.WebhookHasCredential);
    }

    [Fact]
    public async Task SignedWebhookWithMissingSecretDoesNotReportHealthy()
    {
        var credentials = new FakeCredentialStore();
        var health = new FakeHealthService();
        var webhook = new FakeTargetStore<WebhookExportTarget>(
            new("default", new Uri(Endpoint, "hooks/lenxtool"),
                UseHmac: true, CredentialVersion: 1));
        ManagedIntegrationSettingsViewModel viewModel = Create(
            new FakeTargetStore<ReadeckExportTarget>(),
            credentials,
            health,
            webhook);

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.TestWebhookCommand.ExecuteAsync();

        Assert.Equal(0, health.CheckCount);
        Assert.False(viewModel.WebhookHasCredential);
        Assert.Contains("缺失", viewModel.WebhookStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointChangeCannotReuseCredentialBoundToOldAuthority()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(EntryIntegrationKind.Readeck, "old-token");
        var readeck = new FakeTargetStore<ReadeckExportTarget>(
            new("default", Endpoint, Archive: false, CredentialVersion: 1));
        ManagedIntegrationSettingsViewModel viewModel = Create(
            readeck,
            credentials,
            new FakeHealthService());
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.ReadeckEndpoint = "https://other.example.com/";

        await viewModel.SaveReadeckCommand.ExecuteAsync();

        Assert.Equal(0, readeck.Target!.CredentialVersion);
        Assert.False(viewModel.ReadeckHasCredential);
        Assert.Equal("old-token", credentials.Get(EntryIntegrationKind.Readeck));
        Assert.True(viewModel.DeleteReadeckCredentialCommand.CanExecute(null));

        await viewModel.DeleteReadeckCredentialCommand.ExecuteAsync();

        Assert.Null(credentials.Get(EntryIntegrationKind.Readeck));
    }

    [Fact]
    public async Task TestConnectionRejectsUnsavedEndpointWithoutCallingHealth()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(EntryIntegrationKind.Readeck, "saved-token");
        var health = new FakeHealthService();
        var readeck = new FakeTargetStore<ReadeckExportTarget>(
            new("default", Endpoint, Archive: false, CredentialVersion: 1));
        ManagedIntegrationSettingsViewModel viewModel = Create(
            readeck,
            credentials,
            health);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.ReadeckEndpoint = "https://other.example.com/";

        await viewModel.TestReadeckCommand.ExecuteAsync();

        Assert.Equal(0, health.CheckCount);
        Assert.Contains("尚未保存", viewModel.ReadeckStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteCredentialDeactivatesTargetBeforeDeletingSecret()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(EntryIntegrationKind.Readeck, "saved-token");
        var readeck = new FakeTargetStore<ReadeckExportTarget>(
            new("default", Endpoint, Archive: false, CredentialVersion: 1));
        credentials.BeforeDelete = () =>
            Assert.Equal(0, readeck.Target!.CredentialVersion);
        ManagedIntegrationSettingsViewModel viewModel = Create(
            readeck,
            credentials,
            new FakeHealthService());
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.DeleteReadeckCredentialCommand.ExecuteAsync();

        Assert.Equal(0, readeck.Target!.CredentialVersion);
        Assert.Null(credentials.Get(EntryIntegrationKind.Readeck));
        Assert.False(viewModel.ReadeckHasCredential);
    }

    [Fact]
    public async Task LegacyDefaultCredentialWithoutTargetRemainsExplicitlyDeletable()
    {
        var credentials = new FakeCredentialStore();
        credentials.Seed(EntryIntegrationKind.Readeck, "legacy-token");
        var readeck = new FakeTargetStore<ReadeckExportTarget>();
        ManagedIntegrationSettingsViewModel viewModel = Create(
            readeck,
            credentials,
            new FakeHealthService());
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.ReadeckHasCredential);
        Assert.True(viewModel.DeleteReadeckCredentialCommand.CanExecute(null));

        await viewModel.DeleteReadeckCredentialCommand.ExecuteAsync();

        Assert.Null(readeck.Target);
        Assert.Null(credentials.Get(EntryIntegrationKind.Readeck));
        Assert.Contains("删除", viewModel.ReadeckStatus, StringComparison.Ordinal);
    }

    private static ManagedIntegrationSettingsViewModel Create(
        FakeTargetStore<ReadeckExportTarget> readeck,
        FakeCredentialStore credentials,
        FakeHealthService health,
        FakeTargetStore<WebhookExportTarget>? webhook = null) =>
        new(
            readeck,
            new FakeTargetStore<OutlineExportTarget>(),
            new FakeTargetStore<QBittorrentExportTarget>(),
            webhook ?? new FakeTargetStore<WebhookExportTarget>(),
            credentials,
            health);

    private sealed class FakeTargetStore<T>(T? target = null)
        : IIntegrationExportTargetStore<T>
        where T : class
    {
        public T? Target { get; private set; } = target;
        public List<T> SavedTargets { get; } = [];

        public Task<T?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Target);

        public Task<IIntegrationExportTargetLease<T>> AcquireExportLeaseAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IIntegrationExportTargetLease<T>>(
                new FakeLease<T>(Target));

        public Task SaveAsync(T target, CancellationToken cancellationToken)
        {
            Target = target;
            SavedTargets.Add(target);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLease<T>(T? target)
        : IIntegrationExportTargetLease<T>
        where T : class
    {
        public T? Target { get; } = target;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCredentialStore
        : IEntryIntegrationCredentialStore
    {
        private readonly Dictionary<EntryIntegrationKind, string> _values = [];
        public int ExistsCount { get; private set; }
        public Action? BeforeDelete { get; set; }

        public void Seed(EntryIntegrationKind kind, string value) =>
            _values[kind] = value;

        public string? Get(EntryIntegrationKind kind) =>
            _values.GetValueOrDefault(kind);

        public Task<string?> GetAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Get(kind));

        public Task<bool> ExistsAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            ExistsCount++;
            return Task.FromResult(_values.ContainsKey(kind));
        }

        public Task SetAsync(
            EntryIntegrationKind kind,
            string targetId,
            string newValue,
            CancellationToken cancellationToken)
        {
            _values[kind] = newValue;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            EntryIntegrationKind kind,
            string targetId,
            CancellationToken cancellationToken)
        {
            BeforeDelete?.Invoke();
            _values.Remove(kind);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHealthService : IEntryIntegrationHealthService
    {
        public int CheckCount { get; private set; }
        public EntryIntegrationTarget? LastTarget { get; private set; }

        public Task<EntryIntegrationHealthResult> CheckAsync(
            EntryIntegrationTarget target,
            CancellationToken cancellationToken)
        {
            CheckCount++;
            LastTarget = target;
            return Task.FromResult(new EntryIntegrationHealthResult(
                EntryIntegrationHealthStatus.Healthy,
                DateTimeOffset.Parse(
                    "2026-08-13T00:00:00Z",
                    System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
