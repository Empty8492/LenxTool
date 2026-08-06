using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedDigestScheduleServiceTests
{
    [Fact]
    public async Task SavingAnEnabledScheduleDisablesOldGenerationBeforeAtomicPayloadSave()
    {
        LocalScheduledTask current = TaskSnapshot(isEnabled: true);
        var calls = new List<string>();
        var schedules = new RecordingScheduleRepository(current, calls);
        var service = new FeedDigestScheduleService(
            schedules,
            new StubCatalogRepository(ActiveCatalog()),
            new ManualTimeProvider(Utc(2026, 8, 6, 2, 0)));

        FeedDigestScheduleState saved = await service.SaveAsync(
            new(
                FeedDigestPeriod.Daily,
                new TimeOnly(9, 30),
                WeeklyDay: null,
                "UTC",
                IsEnabled: true,
                new(null, null, "人工智能")),
            CancellationToken.None);

        Assert.Equal(["disable", "save"], calls);
        Assert.True(saved.IsEnabled);
        Assert.Equal(new TimeOnly(9, 30), saved.LocalTime);
        Assert.Equal("人工智能", saved.Scope.SearchText);
        Assert.True(schedules.LastSavedAt > schedules.LastDisabledAt);
        Assert.Equal(
            "人工智能",
            FeedDigestScopePayload.Deserialize(
                schedules.Current!.Payload).SearchText);
    }

    [Fact]
    public async Task AtomicSaveFailureLeavesOldScheduleDisabled()
    {
        LocalScheduledTask current = TaskSnapshot(isEnabled: true);
        var calls = new List<string>();
        var schedules = new RecordingScheduleRepository(current, calls);
        schedules.SaveFailure = new IOException("atomic save failed");
        var service = new FeedDigestScheduleService(
            schedules,
            new StubCatalogRepository(ActiveCatalog()),
            new ManualTimeProvider(Utc(2026, 8, 6, 2, 0)));

        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(
            new(
                FeedDigestPeriod.Daily,
                new TimeOnly(9, 30),
                WeeklyDay: null,
                "UTC",
                IsEnabled: true,
                FeedDigestScope.AllActive),
            CancellationToken.None));

        Assert.Equal(["disable", "save"], calls);
        Assert.False(schedules.Current!.IsEnabled);
    }

    [Fact]
    public async Task InactiveSpecificFeedIsRejectedBeforeChangingSchedule()
    {
        var calls = new List<string>();
        var schedules = new RecordingScheduleRepository(
            TaskSnapshot(isEnabled: true),
            calls);
        var service = new FeedDigestScheduleService(
            schedules,
            new StubCatalogRepository(ActiveCatalog()),
            new ManualTimeProvider(Utc(2026, 8, 6, 2, 0)));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(
            new(
                FeedDigestPeriod.Daily,
                new TimeOnly(9, 30),
                WeeklyDay: null,
                "UTC",
                IsEnabled: true,
                new(
                    "a0000000-0000-0000-0000-000000000099",
                    null,
                    null)),
            CancellationToken.None));

        Assert.Empty(calls);
        Assert.True(schedules.Current!.IsEnabled);
    }

    [Fact]
    public async Task InactiveSpecificFeedDoesNotBlockDisablingSchedule()
    {
        var calls = new List<string>();
        var schedules = new RecordingScheduleRepository(
            TaskSnapshot(isEnabled: true),
            calls);
        var service = new FeedDigestScheduleService(
            schedules,
            new StubCatalogRepository(ActiveCatalog()),
            new ManualTimeProvider(Utc(2026, 8, 6, 2, 0)));

        FeedDigestScheduleState saved = await service.SaveAsync(
            new(
                FeedDigestPeriod.Daily,
                new TimeOnly(9, 30),
                WeeklyDay: null,
                "UTC",
                IsEnabled: false,
                new(
                    "a0000000-0000-0000-0000-000000000099",
                    null,
                    null)),
            CancellationToken.None);

        Assert.Equal(["disable", "save"], calls);
        Assert.False(saved.IsEnabled);
        Assert.Null(saved.NextRunAtUtc);
    }

    [Fact]
    public async Task MissingScheduleLoadsDisabledLocalDefaults()
    {
        var service = new FeedDigestScheduleService(
            new RecordingScheduleRepository(null, []),
            new StubCatalogRepository(ActiveCatalog()),
            new ManualTimeProvider(Utc(2026, 8, 6, 2, 0)));

        FeedDigestScheduleState state = await service.GetAsync(
            FeedDigestPeriod.Weekly,
            CancellationToken.None);

        Assert.False(state.IsEnabled);
        Assert.Equal(new TimeOnly(8, 0), state.LocalTime);
        Assert.Equal(DayOfWeek.Monday, state.WeeklyDay);
        Assert.Equal(TimeZoneInfo.Local.Id, state.TimeZoneId);
        Assert.Null(state.NextRunAtUtc);
        Assert.Equal(FeedDigestScope.AllActive, state.Scope);
    }

    private static LocalScheduledTask TaskSnapshot(bool isEnabled) =>
        new(
            FeedDigestScheduleIds.Daily,
            new(
                LocalScheduleFrequency.Daily,
                "UTC",
                new TimeOnly(8, 0)),
            LocalScheduleMissedRunPolicy.RunOnce,
            isEnabled,
            isEnabled ? Utc(2026, 8, 7, 8, 0) : null,
            Utc(2026, 8, 1, 0, 0),
            Utc(2026, 8, 5, 0, 0),
            FeedDigestScopePayload.Serialize(
                FeedDigestScope.AllActive));

    private static FeedCatalogSnapshot ActiveCatalog()
    {
        DateTimeOffset now = Utc(2026, 8, 1, 0, 0);
        var category = new FeedCategory(
            "b0000000-0000-0000-0000-000000000001",
            "技术",
            "技术",
            0,
            true,
            1,
            now,
            now);
        var feed = new FeedCatalogItem(
            "a0000000-0000-0000-0000-000000000001",
            "https://example.test/feed.xml",
            "https://example.test/feed.xml",
            "示例订阅",
            "https://example.test/",
            category.Id,
            FeedViewKind.Article,
            30,
            0,
            true,
            1,
            now,
            now);
        return new(
            new(1, FeedCatalogScope.Active, now, now),
            [category],
            [feed]);
    }

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private sealed class RecordingScheduleRepository(
        LocalScheduledTask? current,
        List<string> calls) : ILocalScheduledTaskRepository
    {
        public LocalScheduledTask? Current { get; private set; } = current;
        public DateTimeOffset LastDisabledAt { get; private set; }
        public DateTimeOffset LastSavedAt { get; private set; }
        public Exception? SaveFailure { get; set; }

        public Task<LocalScheduledTask> SaveAsync(
            string id,
            LocalScheduleDefinition schedule,
            LocalScheduleMissedRunPolicy missedRunPolicy,
            bool isEnabled,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken,
            string? payload = null)
        {
            calls.Add("save");
            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }
            LastSavedAt = changedAtUtc;
            Current = new(
                id,
                schedule,
                missedRunPolicy,
                isEnabled,
                isEnabled ? changedAtUtc.AddDays(1) : null,
                Current?.CreatedAtUtc ?? changedAtUtc,
                changedAtUtc,
                payload);
            return Task.FromResult(Current);
        }

        public Task<LocalScheduledTask?> GetAsync(
            string id,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                string.Equals(Current?.Id, id, StringComparison.Ordinal)
                    ? Current
                    : null);

        public Task<IReadOnlyList<LocalScheduledTask>> GetAllAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalScheduledTask>>(
                Current is null ? [] : [Current]);

        public Task<LocalScheduledTask?> SetEnabledAsync(
            string id,
            bool isEnabled,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken)
        {
            calls.Add("disable");
            LastDisabledAt = changedAtUtc;
            Current = Current! with
            {
                IsEnabled = isEnabled,
                NextRunAtUtc = null,
                UpdatedAtUtc = changedAtUtc
            };
            return Task.FromResult<LocalScheduledTask?>(Current);
        }
    }

    private sealed class StubCatalogRepository(FeedCatalogSnapshot snapshot)
        : IFeedCatalogRepository
    {
        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(snapshot);

        public Task ReplaceAsync(
            FeedCatalogSnapshot value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
