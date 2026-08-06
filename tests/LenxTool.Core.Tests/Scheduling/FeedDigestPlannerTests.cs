using LenxTool.Core.Models;
using LenxTool.Core.Scheduling;

namespace LenxTool.Core.Tests.Scheduling;

public sealed class FeedDigestPlannerTests
{
    [Fact]
    public void DailyWindowUsesLocalCalendarAcrossSpringDstGap()
    {
        string zoneId = OperatingSystem.IsWindows()
            ? "Eastern Standard Time"
            : "America/New_York";
        var schedule = new LocalScheduleDefinition(
            LocalScheduleFrequency.Daily,
            zoneId,
            new TimeOnly(2, 30));

        FeedDigestWindow window = FeedDigestPlanner.GetWindow(
            FeedDigestPeriod.Daily,
            schedule,
            Utc(2026, 3, 8, 7, 0));

        // DST 缺口当天的 02:30 会前移到 03:00；窗口起点仍是前一自然日 02:30，
        // 因而不能把“每日”错误实现为固定减去 24 小时。
        Assert.Equal(Utc(2026, 3, 7, 7, 30), window.StartUtc);
        Assert.Equal(Utc(2026, 3, 8, 7, 0), window.EndUtc);
    }

    [Fact]
    public void WeeklyWindowUsesSevenLocalCalendarDays()
    {
        var schedule = new LocalScheduleDefinition(
            LocalScheduleFrequency.Weekly,
            "UTC",
            new TimeOnly(9, 0),
            WeeklyDay: DayOfWeek.Monday);

        FeedDigestWindow window = FeedDigestPlanner.GetWindow(
            FeedDigestPeriod.Weekly,
            schedule,
            Utc(2026, 8, 10, 9, 0));

        Assert.Equal(Utc(2026, 8, 3, 9, 0), window.StartUtc);
        Assert.Equal(Utc(2026, 8, 10, 9, 0), window.EndUtc);
    }

    [Fact]
    public void CreatePlanDeduplicatesAndBoundsEntriesDeterministically()
    {
        FeedDigestOptions options = FeedDigestOptions.Default with
        {
            MaximumEntries = 2,
            MaximumCharactersPerEntry = 12,
            MaximumSourceCharacters = 180
        };
        var window = new FeedDigestWindow(
            Utc(2026, 8, 5, 0, 0),
            Utc(2026, 8, 6, 0, 0));
        FeedDigestScope scope = FeedDigestScope.AllActive;
        FeedEntry[] entries =
        [
            Entry(
                "00000000-0000-0000-0000-000000000003",
                "https://example.test/shared",
                "较旧的重复条目",
                "这段正文不应进入最终输入",
                Utc(2026, 8, 5, 8, 0),
                "hash-3"),
            Entry(
                "00000000-0000-0000-0000-000000000002",
                "https://example.test/second",
                "第二条",
                "abcdefghijklmnopqrstuv",
                Utc(2026, 8, 5, 10, 0),
                "hash-2"),
            Entry(
                "00000000-0000-0000-0000-000000000001",
                "https://example.test/shared",
                "最新的重复条目",
                "12345678901234567890",
                Utc(2026, 8, 5, 11, 0),
                "hash-1")
        ];

        FeedDigestPlan first = Assert.IsType<FeedDigestPlan>(
            FeedDigestPlanner.CreatePlan(
                FeedDigestPeriod.Daily,
                FeedDigestScheduleIds.Daily,
                scope,
                window,
                entries,
                options));
        FeedDigestPlan reordered = Assert.IsType<FeedDigestPlan>(
            FeedDigestPlanner.CreatePlan(
                FeedDigestPeriod.Daily,
                FeedDigestScheduleIds.Daily,
                scope,
                window,
                entries.Reverse().ToArray(),
                options));

        Assert.Equal(2, first.EntryCount);
        Assert.DoesNotContain("较旧的重复条目", first.SourceContent, StringComparison.Ordinal);
        Assert.Contains("最新的重复条目", first.SourceContent, StringComparison.Ordinal);
        Assert.True(first.SourceContent.Length <= options.MaximumSourceCharacters);
        Assert.Equal(first.ReportId, reordered.ReportId);
        Assert.Equal(first.ContentHash, reordered.ContentHash);
        Assert.Equal(first.SourceContent, reordered.SourceContent);
    }

    [Fact]
    public void CreatePlanReturnsNullForAnEmptyWindow()
    {
        FeedDigestPlan? plan = FeedDigestPlanner.CreatePlan(
            FeedDigestPeriod.Daily,
            FeedDigestScheduleIds.Daily,
            FeedDigestScope.AllActive,
            new(
                Utc(2026, 8, 5, 0, 0),
                Utc(2026, 8, 6, 0, 0)),
            [],
            FeedDigestOptions.Default);

        Assert.Null(plan);
    }

    [Fact]
    public void CacheIdentityChangesWithScopeContentOrPromptVersion()
    {
        var window = new FeedDigestWindow(
            Utc(2026, 8, 5, 0, 0),
            Utc(2026, 8, 6, 0, 0));
        FeedEntry entry = Entry(
            "00000000-0000-0000-0000-000000000001",
            "https://example.test/one",
            "标题",
            "摘要",
            Utc(2026, 8, 5, 11, 0),
            "hash-1");
        FeedDigestPlan baseline = Assert.IsType<FeedDigestPlan>(
            FeedDigestPlanner.CreatePlan(
                FeedDigestPeriod.Daily,
                FeedDigestScheduleIds.Daily,
                FeedDigestScope.AllActive,
                window,
                [entry],
                FeedDigestOptions.Default));
        FeedDigestPlan scoped = Assert.IsType<FeedDigestPlan>(
            FeedDigestPlanner.CreatePlan(
                FeedDigestPeriod.Daily,
                FeedDigestScheduleIds.Daily,
                new(null, null, "人工智能"),
                window,
                [entry],
                FeedDigestOptions.Default));
        FeedDigestPlan changedContent = Assert.IsType<FeedDigestPlan>(
            FeedDigestPlanner.CreatePlan(
                FeedDigestPeriod.Daily,
                FeedDigestScheduleIds.Daily,
                FeedDigestScope.AllActive,
                window,
                [entry with { Summary = "更新后的摘要" }],
                FeedDigestOptions.Default));
        FeedDigestPlan changedPrompt = Assert.IsType<FeedDigestPlan>(
            FeedDigestPlanner.CreatePlan(
                FeedDigestPeriod.Daily,
                FeedDigestScheduleIds.Daily,
                FeedDigestScope.AllActive,
                window,
                [entry],
                FeedDigestOptions.Default with { PromptVersion = "feed-digest-v2" }));

        Assert.NotEqual(baseline.ReportId, scoped.ReportId);
        Assert.NotEqual(baseline.ReportId, changedContent.ReportId);
        Assert.NotEqual(baseline.ReportId, changedPrompt.ReportId);
    }

    [Fact]
    public void CacheIdentityIgnoresEntriesThatDoNotReachBoundedModelInput()
    {
        FeedDigestOptions options = FeedDigestOptions.Default with
        {
            MaximumEntries = 3,
            MaximumSourceCharacters = 128
        };
        var window = new FeedDigestWindow(
            Utc(2026, 8, 5, 0, 0),
            Utc(2026, 8, 6, 0, 0));
        FeedEntry first = Entry(
            "00000000-0000-0000-0000-000000000001",
            "https://example.test/one",
            "第一条",
            new string('甲', 300),
            Utc(2026, 8, 5, 11, 0),
            "hash-1");
        FeedEntry tail = Entry(
            "00000000-0000-0000-0000-000000000002",
            "https://example.test/two",
            "未进入输入的第二条",
            "原始尾部",
            Utc(2026, 8, 5, 10, 0),
            "hash-2");

        FeedDigestPlan baseline = Assert.IsType<FeedDigestPlan>(
            FeedDigestPlanner.CreatePlan(
                FeedDigestPeriod.Daily,
                FeedDigestScheduleIds.Daily,
                FeedDigestScope.AllActive,
                window,
                [first, tail],
                options));
        FeedDigestPlan changedTail = Assert.IsType<FeedDigestPlan>(
            FeedDigestPlanner.CreatePlan(
                FeedDigestPeriod.Daily,
                FeedDigestScheduleIds.Daily,
                FeedDigestScope.AllActive,
                window,
                [first, tail with { Summary = "完全不同但仍未进入模型的尾部" }],
                options));

        Assert.Equal(1, baseline.EntryCount);
        Assert.Equal(baseline.SourceContent, changedTail.SourceContent);
        Assert.Equal(baseline.ContentHash, changedTail.ContentHash);
        Assert.Equal(baseline.ReportId, changedTail.ReportId);
    }

    [Fact]
    public void ScopePayloadRoundTripsAndUnknownOrMissingVersionFailsClosed()
    {
        var expected = new FeedDigestScope(
            "10000000-0000-4000-8000-000000000001",
            null,
            "  AI security  ");

        FeedDigestScope actual = FeedDigestScopePayload.Deserialize(
            FeedDigestScopePayload.Serialize(expected));

        Assert.Equal(
            "10000000-0000-4000-8000-000000000001",
            actual.FeedId);
        Assert.Equal("AI security", actual.SearchText);
        Assert.Throws<InvalidDataException>(() =>
            FeedDigestScopePayload.Deserialize(null));
        Assert.Throws<InvalidDataException>(() =>
            FeedDigestScopePayload.Deserialize(
                "{\"version\":2,\"feedId\":null," +
                "\"categoryId\":null,\"searchText\":null}"));
    }

    private static FeedEntry Entry(
        string id,
        string? url,
        string title,
        string summary,
        DateTimeOffset publishedAt,
        string contentHash) =>
        new(
            id,
            "10000000-0000-0000-0000-000000000001",
            id,
            url,
            title,
            null,
            publishedAt,
            null,
            summary,
            string.Empty,
            [],
            [],
            contentHash,
            publishedAt);

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
