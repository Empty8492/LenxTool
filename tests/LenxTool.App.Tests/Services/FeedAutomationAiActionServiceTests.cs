using System.Collections.Concurrent;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationAiActionServiceTests
{
    private const string FeedId =
        "30000000-0000-4000-8000-000000000401";
    private const string CategoryId =
        "20000000-0000-4000-8000-000000000401";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RuleSummaryExecutesWhenPolicyAutoSwitchIsDisabled()
    {
        var jobs = new StubJobRepository();
        var summaries = new StubSummaryService();
        var service = CreateService(
            jobs,
            summaries: summaries);

        FeedAutomationAiActionResult result = await service.ExecuteAsync(
            Lease(FeedAutomationActionType.GenerateSummary),
            CancellationToken.None);

        Assert.Equal(FeedAutomationAiActionResult.Completed, result);
        FeedAiSummaryInput input = Assert.Single(summaries.Inputs);
        Assert.Equal("entry-401", input.EntryId);
        Assert.Equal(new string('a', 64), input.ContentHash);
        Assert.Equal(1, jobs.ReserveCalls);
        Assert.Equal(20, jobs.LastDailyLimit);
    }

    [Fact]
    public async Task RuleTranslationUsesActionTargetLanguage()
    {
        var jobs = new StubJobRepository();
        var translations = new StubTranslationService();
        var service = CreateService(
            jobs,
            translations: translations);

        FeedAutomationAiActionResult result = await service.ExecuteAsync(
            Lease(
                FeedAutomationActionType.Translate,
                value: "ja"),
            CancellationToken.None);

        Assert.Equal(FeedAutomationAiActionResult.Completed, result);
        Assert.Equal(
            "ja",
            Assert.Single(translations.Inputs).TargetLanguage);
        Assert.Equal(1, jobs.ReserveCalls);
    }

    [Fact]
    public async Task DailyLimitReturnsRetryableBoundaryWithoutCallingAi()
    {
        var jobs = new StubJobRepository
        {
            ReserveResult = false
        };
        var summaries = new StubSummaryService();
        var service = CreateService(
            jobs,
            summaries: summaries);

        AppException exception = await Assert.ThrowsAsync<AppException>(
            () => service.ExecuteAsync(
                Lease(FeedAutomationActionType.GenerateSummary),
                CancellationToken.None));

        Assert.Equal(
            AppErrorCode.ProviderRateLimited,
            exception.Error.Code);
        Assert.True(exception.Error.IsRetryable);
        Assert.Equal(
            TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(1)),
            exception.Error.RetryAfter);
        Assert.Empty(summaries.Inputs);
    }

    [Fact]
    public async Task MissingOrDisabledFeedReturnsTerminalResult()
    {
        var jobs = new StubJobRepository();
        FeedAutomationAiActionService missingEntry = CreateService(
            jobs,
            entryExists: false);

        FeedAutomationAiActionResult missing =
            await missingEntry.ExecuteAsync(
                Lease(FeedAutomationActionType.GenerateSummary),
                CancellationToken.None);

        Assert.Equal(
            FeedAutomationAiActionResult.EntryMissing,
            missing);

        FeedAutomationAiActionService disabled = CreateService(
            jobs,
            feedEnabled: false);
        FeedAutomationAiActionResult unavailable =
            await disabled.ExecuteAsync(
                Lease(FeedAutomationActionType.GenerateSummary),
                CancellationToken.None);

        Assert.Equal(
            FeedAutomationAiActionResult.FeedUnavailable,
            unavailable);
        Assert.Equal(0, jobs.ReserveCalls);
    }

    [Fact]
    public async Task UnsupportedActionIsRejectedBeforeEntryLookup()
    {
        var jobs = new StubJobRepository();
        var entries = new StubEntryRepository(Entry());
        var service = new FeedAutomationAiActionService(
            jobs,
            new StubCatalogRepository(Catalog(feedEnabled: true)),
            entries,
            new StubSummaryService(),
            new StubTranslationService(),
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ExecuteAsync(
                Lease(FeedAutomationActionType.AddTag, value: "unsafe"),
                CancellationToken.None));

        Assert.Equal(0, entries.GetByIdCalls);
        Assert.Equal(0, jobs.ReserveCalls);
    }

    private static FeedAutomationAiActionService CreateService(
        StubJobRepository jobs,
        bool entryExists = true,
        bool feedEnabled = true,
        StubSummaryService? summaries = null,
        StubTranslationService? translations = null) =>
        new(
            jobs,
            new StubCatalogRepository(Catalog(feedEnabled)),
            new StubEntryRepository(entryExists ? Entry() : null),
            summaries ?? new StubSummaryService(),
            translations ?? new StubTranslationService(),
            new FixedTimeProvider(Now));

    private static FeedCatalogSnapshot Catalog(
        bool feedEnabled)
    {
        FeedAiPolicy defaults = FeedAiPolicy.SafeDefaults with
        {
            AutoSummary = FeedAiPolicySwitch.Disabled,
            AutoTranslation = FeedAiPolicySwitch.Disabled,
            DailyEntryLimit = 20,
            MaxConcurrency = 1
        };
        return new(
            new(1, FeedCatalogScope.Active, Now, Now),
            [
                new(
                    CategoryId,
                    "Tech",
                    "tech",
                    0,
                    true,
                    1,
                    Now,
                    Now)
            ],
            [
                new(
                    FeedId,
                    "https://news.example/feed.xml",
                    "https://news.example/feed.xml",
                    "News",
                    null,
                    CategoryId,
                    FeedViewKind.Article,
                    60,
                    0,
                    feedEnabled,
                    1,
                    Now,
                    Now)
            ],
            defaults);
    }

    private static FeedEntry Entry() => new(
        "entry-401",
        FeedId,
        "external-401",
        "https://news.example/articles/401",
        "Article title",
        null,
        Now,
        Now,
        "Fallback summary",
        """
        <h2>Section</h2>
        <p>Hello <a href="https://news.example/world">world</a></p>
        """,
        [],
        [],
        new string('a', 64),
        Now);

    private static FeedAutomationActionLease Lease(
        FeedAutomationActionType type,
        string? value = null) => new(
        new string('a', 64),
        "entry-401",
        "40000000-0000-4000-8000-000000000401",
        1,
        100,
        0,
        type,
        10,
        value,
        1,
        new string('b', 32));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubJobRepository
        : IFeedAiAutomationJobRepository
    {
        public bool ReserveResult { get; init; } = true;
        public int ReserveCalls { get; private set; }
        public int LastDailyLimit { get; private set; }

        public Task<bool> TryReserveDailyEntryAsync(
            DateOnly usageDate,
            string feedId,
            string entryId,
            int dailyEntryLimit,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ReserveCalls++;
            LastDailyLimit = dailyEntryLimit;
            return Task.FromResult(ReserveResult);
        }

        public Task<int> EnqueueAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            ResolvedFeedAiPolicy policy,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FeedAiAutomationJob>> ClaimDueAsync(
            DateTimeOffset now,
            int maximumCount,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteAsync(
            FeedAiAutomationJob job,
            FeedAiAutomationJobOutcome outcome,
            string? errorCode,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ScheduleRetryAsync(
            FeedAiAutomationJob job,
            string errorCode,
            DateTimeOffset nextAttemptAt,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(
            FeedAiAutomationJob job,
            DateTimeOffset releasedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCatalogRepository(
        FeedCatalogSnapshot snapshot)
        : IFeedCatalogRepository
    {
        public Task<FeedCatalogState> GetStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshot.State);

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(snapshot);

        public Task ReplaceAsync(
            FeedCatalogSnapshot value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubEntryRepository(FeedEntry? entry)
        : IFeedEntryRepository
    {
        public int GetByIdCalls { get; private set; }

        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken)
        {
            GetByIdCalls++;
            return Task.FromResult(
                entry?.Id == entryId
                    ? entry
                    : null);
        }

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubSummaryService
        : IFeedAiSummaryService
    {
        public ConcurrentBag<FeedAiSummaryInput> Inputs { get; } = [];

        public Task<FeedAiResult> SummarizeAsync(
            FeedAiSummaryInput input,
            CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            return Task.FromResult(Result(
                input.EntryId,
                input.ContentHash,
                FeedAiTaskType.Summary,
                "und"));
        }

        public Task<IReadOnlyList<FeedAiSummaryBatchItem>>
            SummarizeBatchAsync(
                IReadOnlyList<FeedAiSummaryInput> inputs,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubTranslationService
        : IFeedAiTranslationService
    {
        public ConcurrentBag<FeedAiTranslationInput> Inputs { get; } = [];

        public Task<FeedAiTranslationResult> TranslateAsync(
            FeedAiTranslationInput input,
            CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            return Task.FromResult(new FeedAiTranslationResult(
                Result(
                    input.EntryId,
                    input.ContentHash,
                    FeedAiTaskType.Translation,
                    input.TargetLanguage),
                []));
        }
    }

    private static FeedAiResult Result(
        string entryId,
        string contentHash,
        FeedAiTaskType taskType,
        string targetLanguage) => new(
        Guid.NewGuid().ToString("N"),
        new(
            entryId,
            contentHash,
            taskType,
            targetLanguage,
            "deepseek-v4-flash",
            "test-v1"),
        "title",
        "content",
        1,
        1,
        1,
        2,
        10,
        null,
        Now,
        Now);
}
