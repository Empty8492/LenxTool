using LenxTool.Core.Contracts;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.Infrastructure.Tests.Exports;

/// <summary>
/// 冻结 Eagle 本机目标的持久化边界：端点必须规范化，队列作用域不能泄露地址，
/// 损坏或过期的设置文档必须失败关闭。
/// </summary>
public sealed class EagleExportTargetStoreTests
{
    [Fact]
    public void QueueTargetIdIsStableOpaqueAndBindsEndpointAndLibrary()
    {
        const string LibraryA = "111111111111111111111111";
        const string LibraryB = "222222222222222222222222";
        var target = new EagleExportTarget(
            "default",
            new Uri("http://127.0.0.1:41595/"));

        string first = target.CreateQueueTargetId(LibraryA);
        string repeated = target.CreateQueueTargetId(LibraryA);
        string changed = (target with
        {
            Endpoint = new Uri("http://127.0.0.1:41596/")
        }).CreateQueueTargetId(LibraryA);
        string changedLibrary = target.CreateQueueTargetId(LibraryB);

        Assert.Equal(first, repeated);
        Assert.Matches(
            "^default\\.[0-9a-f]{24}\\.[0-9a-f]{24}$",
            first);
        Assert.NotEqual(first, changed);
        Assert.NotEqual(first, changedLibrary);
        Assert.DoesNotContain("41595", first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("0123456789abcdef0123456z")]
    public void QueueTargetIdRejectsNonOpaqueLibraryRevision(
        string libraryRevision)
    {
        var target = new EagleExportTarget(
            "default",
            new Uri("http://127.0.0.1:41595/"));

        Assert.Throws<ArgumentException>(
            () => target.CreateQueueTargetId(libraryRevision));
    }

    [Fact]
    public async Task SaveAsyncPersistsOneVersionedDocumentAndRoundTripsTarget()
    {
        var settings = new RecordingSettingsRepository();
        var store = new AppSettingsEagleExportTargetStore(settings);

        await store.SaveAsync(
            new(
                "default",
                new Uri("http://127.0.0.1:41595/")),
            CancellationToken.None);
        EagleExportTarget? restored = await store.GetAsync(
            CancellationToken.None);

        KeyValuePair<string, string> write = Assert.Single(settings.Writes);
        Assert.Equal(
            AppSettingsEagleExportTargetStore.SettingsKey,
            write.Key);
        Assert.Contains("\"version\":1", write.Value, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal("default", restored.TargetId);
        Assert.Equal(
            "http://127.0.0.1:41595/",
            restored.Endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task SaveAsyncWaitsForActiveExportGenerationLease()
    {
        // 端点配置是运行时安全边界；保存下一代配置必须等待持有当前快照的
        // 导出释放租约，不能在已有外部副作用尚未结束时提前生效。
        var settings = new RecordingSettingsRepository();
        var store = new AppSettingsEagleExportTargetStore(settings);
        var current = new EagleExportTarget(
            "default",
            new Uri("http://127.0.0.1:41595/"));
        var next = current with
        {
            Endpoint = new Uri("http://127.0.0.1:41596/")
        };
        await store.SaveAsync(current, CancellationToken.None);
        IEagleExportTargetLease lease = await store.AcquireExportLeaseAsync(
            CancellationToken.None);

        Task saving = store.SaveAsync(next, CancellationToken.None);

        Assert.False(saving.IsCompleted);
        Assert.Equal(current, lease.Target);
        Assert.Single(settings.Writes);

        await lease.DisposeAsync();
        await saving;

        EagleExportTarget? restored = await store.GetAsync(
            CancellationToken.None);
        Assert.Equal(next, restored);
        Assert.Equal(2, settings.Writes.Count);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":2,\"target\":null}")]
    [InlineData("{\"version\":1,\"target\":null}")]
    [InlineData("{\"version\":1,\"target\":{\"targetId\":\"default\",\"endpoint\":\"https://127.0.0.1:41595/\"}}")]
    public async Task GetAsyncFailsClosedForMalformedUnsupportedOrUnsafeDocuments(
        string stored)
    {
        var store = new AppSettingsEagleExportTargetStore(
            new RecordingSettingsRepository
            {
                StoredValue = stored
            });

        EagleExportTarget? result = await store.GetAsync(
            CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class RecordingSettingsRepository
        : IAppSettingsRepository
    {
        public string? StoredValue { get; set; }

        public List<KeyValuePair<string, string>> Writes { get; } = [];

        public Task<string?> GetAsync(
            string key,
            CancellationToken cancellationToken) =>
            Task.FromResult(StoredValue);

        public Task SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken)
        {
            Writes.Add(new(key, value));
            StoredValue = value;
            return Task.CompletedTask;
        }
    }
}
