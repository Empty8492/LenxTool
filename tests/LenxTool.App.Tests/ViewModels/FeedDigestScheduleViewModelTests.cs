using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.ViewModels;

public sealed class FeedDigestScheduleViewModelTests
{
    private const string ActiveCategoryId = "10000000-0000-4000-8000-000000000001";
    private const string ActiveFeedId = "30000000-0000-4000-8000-000000000001";
    private const string DisabledFeedId = "30000000-0000-4000-8000-000000000002";

    [Fact]
    public async Task InitializeLoadsActiveCatalogAndExistingSchedules()
    {
        var schedules = new StubScheduleService
        {
            Daily = State(
                FeedDigestPeriod.Daily,
                new TimeOnly(7, 30),
                null,
                true,
                new(null, ActiveCategoryId, "AI")),
            Weekly = State(
                FeedDigestPeriod.Weekly,
                new TimeOnly(9, 15),
                DayOfWeek.Friday,
                false,
                new(ActiveFeedId, ActiveCategoryId, null))
        };
        using var viewModel = new FeedDigestScheduleViewModel(
            schedules,
            new StubCatalogRepository(CreateCatalog()));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(3, viewModel.ScopeChoices.Count);
        Assert.DoesNotContain(
            viewModel.ScopeChoices,
            choice => choice.Id == DisabledFeedId);
        Assert.Equal(ActiveCategoryId, viewModel.DailySelectedScope.Id);
        Assert.Equal("AI", viewModel.DailySearchText);
        Assert.True(viewModel.DailyEnabled);
        Assert.Equal("07:30", viewModel.DailyTimeText);
        Assert.Equal(ActiveFeedId, viewModel.WeeklySelectedScope.Id);
        Assert.Equal(DayOfWeek.Friday, viewModel.SelectedWeeklyDay.Value);
        Assert.Contains("下一次", viewModel.DailyNextRunText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveDailyBuildsNormalizedLocalConfiguration()
    {
        var schedules = new StubScheduleService();
        using var viewModel = new FeedDigestScheduleViewModel(
            schedules,
            new StubCatalogRepository(CreateCatalog()));
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.DailyEnabled = true;
        viewModel.DailyTimeText = "06:45";
        viewModel.DailySelectedScope = Assert.Single(
            viewModel.ScopeChoices,
            choice => choice.Id == ActiveFeedId);
        viewModel.DailySearchText = "  machine learning  ";

        await viewModel.SaveDailyCommand.ExecuteAsync();

        FeedDigestScheduleConfiguration saved = Assert.Single(schedules.Saved);
        Assert.Equal(FeedDigestPeriod.Daily, saved.Period);
        Assert.Equal(new TimeOnly(6, 45), saved.LocalTime);
        Assert.Null(saved.WeeklyDay);
        Assert.True(saved.IsEnabled);
        Assert.Equal(ActiveFeedId, saved.Scope.FeedId);
        Assert.Equal(ActiveCategoryId, saved.Scope.CategoryId);
        Assert.Equal("machine learning", saved.Scope.SearchText);
        Assert.Contains("已保存", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidTimeDoesNotMutateSchedule()
    {
        var schedules = new StubScheduleService();
        using var viewModel = new FeedDigestScheduleViewModel(
            schedules,
            new StubCatalogRepository(CreateCatalog()));
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.WeeklyTimeText = "25:80";

        await viewModel.SaveWeeklyCommand.ExecuteAsync();

        Assert.Empty(schedules.Saved);
        Assert.Contains("HH:mm", viewModel.Status, StringComparison.Ordinal);
    }

    private static FeedDigestScheduleState State(
        FeedDigestPeriod period,
        TimeOnly time,
        DayOfWeek? day,
        bool enabled,
        FeedDigestScope scope) =>
        new(
            period,
            time,
            day,
            TimeZoneInfo.Local.Id,
            enabled,
            enabled
                ? new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero)
                : null,
            scope);

    private static FeedCatalogSnapshot CreateCatalog()
    {
        DateTimeOffset now = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        return new(
            new(1, FeedCatalogScope.Active, now, now),
            [
                new(
                    ActiveCategoryId,
                    "技术",
                    "技术",
                    1,
                    true,
                    1,
                    now,
                    now)
            ],
            [
                new(
                    ActiveFeedId,
                    "https://example.test/active.xml",
                    "https://example.test/active.xml",
                    "Active Feed",
                    "https://example.test",
                    ActiveCategoryId,
                    FeedViewKind.Article,
                    30,
                    1,
                    true,
                    1,
                    now,
                    now),
                new(
                    DisabledFeedId,
                    "https://example.test/disabled.xml",
                    "https://example.test/disabled.xml",
                    "Disabled Feed",
                    "https://example.test",
                    ActiveCategoryId,
                    FeedViewKind.Article,
                    30,
                    2,
                    false,
                    1,
                    now,
                    now)
            ]);
    }

    private sealed class StubScheduleService : IFeedDigestScheduleService
    {
        public FeedDigestScheduleState Daily { get; set; } = State(
            FeedDigestPeriod.Daily,
            new TimeOnly(8, 0),
            null,
            false,
            FeedDigestScope.AllActive);

        public FeedDigestScheduleState Weekly { get; set; } = State(
            FeedDigestPeriod.Weekly,
            new TimeOnly(8, 0),
            DayOfWeek.Monday,
            false,
            FeedDigestScope.AllActive);

        public List<FeedDigestScheduleConfiguration> Saved { get; } = [];

        public Task<FeedDigestScheduleState> GetAsync(
            FeedDigestPeriod period,
            CancellationToken cancellationToken) =>
            Task.FromResult(period == FeedDigestPeriod.Daily ? Daily : Weekly);

        public Task<FeedDigestScheduleState> SaveAsync(
            FeedDigestScheduleConfiguration configuration,
            CancellationToken cancellationToken)
        {
            Saved.Add(configuration);
            FeedDigestScheduleState state = State(
                configuration.Period,
                configuration.LocalTime,
                configuration.WeeklyDay,
                configuration.IsEnabled,
                configuration.Scope);
            if (configuration.Period == FeedDigestPeriod.Daily)
            {
                Daily = state;
            }
            else
            {
                Weekly = state;
            }
            return Task.FromResult(state);
        }
    }

    private sealed class StubCatalogRepository(FeedCatalogSnapshot snapshot)
        : IFeedCatalogRepository
    {
        public Task ReplaceAsync(
            FeedCatalogSnapshot value,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) => Task.FromResult<FeedCatalogSnapshot?>(snapshot);

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogState> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot.State);
    }
}
