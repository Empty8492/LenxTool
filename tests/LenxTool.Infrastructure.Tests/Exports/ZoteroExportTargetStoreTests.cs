using System.Globalization;
using LenxTool.Core.Contracts;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.Infrastructure.Tests.Exports;

/// <summary>
/// 冻结 Zotero 个人库目标的本机持久化与队列代际边界；API key 不属于该文档。
/// </summary>
public sealed class ZoteroExportTargetStoreTests
{
    [Fact]
    public void QueueTargetIdIsStableOpaqueAndBindsEveryOption()
    {
        ZoteroExportTarget target = ValidTarget();

        string first = target.CreateQueueTargetId();
        string repeated = target.CreateQueueTargetId();
        ZoteroExportTarget[] changedTargets =
        [
            target with { UserId = target.UserId + 1 },
            target with { ItemType = ZoteroItemType.JournalArticle },
            target with { IncludeSummaryNote = false },
            target with { UploadFirstImageAttachment = true }
        ];

        Assert.Equal(first, repeated);
        Assert.Matches("^default\\.[0-9a-f]{24}$", first);
        Assert.All(
            changedTargets,
            changed => Assert.NotEqual(
                first,
                changed.CreateQueueTargetId()));
        Assert.DoesNotContain(
            target.UserId.ToString(CultureInfo.InvariantCulture),
            first,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApiRootIsAlwaysTheOfficialPersonalLibraryRoot()
    {
        ZoteroExportTarget target = ValidTarget() with
        {
            UserId = 99887766
        };

        Assert.Equal(
            "https://api.zotero.org/users/99887766/",
            target.ApiRoot.AbsoluteUri);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void QueueTargetRejectsNonPositiveUserId(long userId)
    {
        ZoteroExportTarget target = ValidTarget() with
        {
            UserId = userId
        };

        Assert.Throws<ArgumentException>(target.CreateQueueTargetId);
    }

    [Fact]
    public async Task SavePersistsOneVersionedDocumentWithoutCredentialAndRoundTrips()
    {
        var settings = new RecordingSettingsRepository();
        var store = new AppSettingsZoteroExportTargetStore(settings);
        ZoteroExportTarget target = ValidTarget();

        await store.SaveAsync(target, CancellationToken.None);
        ZoteroExportTarget? restored = await store.GetAsync(
            CancellationToken.None);

        KeyValuePair<string, string> write = Assert.Single(settings.Writes);
        Assert.Equal(
            AppSettingsZoteroExportTargetStore.SettingsKey,
            write.Key);
        Assert.Contains("\"version\":1", write.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", write.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(target, restored);
    }

    [Fact]
    public async Task SaveWaitsForActiveExportLease()
    {
        var settings = new RecordingSettingsRepository();
        var store = new AppSettingsZoteroExportTargetStore(settings);
        ZoteroExportTarget current = ValidTarget();
        ZoteroExportTarget next = current with
        {
            UserId = current.UserId + 1
        };
        await store.SaveAsync(current, CancellationToken.None);
        IZoteroExportTargetLease lease =
            await store.AcquireExportLeaseAsync(CancellationToken.None);

        Task saving = store.SaveAsync(next, CancellationToken.None);

        Assert.False(saving.IsCompleted);
        Assert.Equal(current, lease.Target);
        Assert.Single(settings.Writes);

        await lease.DisposeAsync();
        await saving;

        Assert.Equal(2, settings.Writes.Count);
        Assert.Equal(
            next,
            await store.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExportLeaseMayBeReleasedTwiceWithoutOpeningAnotherPermit()
    {
        var settings = new RecordingSettingsRepository();
        var store = new AppSettingsZoteroExportTargetStore(settings);
        await store.SaveAsync(ValidTarget(), CancellationToken.None);
        IZoteroExportTargetLease lease =
            await store.AcquireExportLeaseAsync(CancellationToken.None);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        IZoteroExportTargetLease nextLease =
            await store.AcquireExportLeaseAsync(CancellationToken.None);
        Task<IZoteroExportTargetLease> blocked =
            store.AcquireExportLeaseAsync(CancellationToken.None);
        Assert.False(blocked.IsCompleted);

        await nextLease.DisposeAsync();
        await using IZoteroExportTargetLease finalLease = await blocked;
        Assert.NotNull(finalLease.Target);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":2,\"target\":null}")]
    [InlineData("{\"version\":1,\"target\":null}")]
    [InlineData("{\"version\":1,\"target\":{\"targetId\":\"other\",\"userId\":42,\"itemType\":1,\"includeSummaryNote\":true,\"uploadFirstImageAttachment\":false}}")]
    [InlineData("{\"version\":1,\"target\":{\"targetId\":\"default\",\"userId\":0,\"itemType\":1,\"includeSummaryNote\":true,\"uploadFirstImageAttachment\":false}}")]
    [InlineData("{\"version\":1,\"target\":{\"targetId\":\"default\",\"userId\":42,\"itemType\":999,\"includeSummaryNote\":true,\"uploadFirstImageAttachment\":false}}")]
    public async Task GetFailsClosedForMalformedUnsupportedOrUnsafeDocuments(
        string stored)
    {
        var store = new AppSettingsZoteroExportTargetStore(
            new RecordingSettingsRepository
            {
                StoredValue = stored
            });

        ZoteroExportTarget? result = await store.GetAsync(
            CancellationToken.None);

        Assert.Null(result);
    }

    private static ZoteroExportTarget ValidTarget() => new(
        ZoteroExportTarget.DefaultTargetId,
        12345678,
        ZoteroItemType.Webpage,
        IncludeSummaryNote: true,
        UploadFirstImageAttachment: false);

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
