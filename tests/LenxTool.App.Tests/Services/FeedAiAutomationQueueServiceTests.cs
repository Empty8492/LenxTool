using System.Collections.Concurrent;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAiAutomationQueueServiceTests
{
    private const string CategoryId = "10000000-0000-4000-8000-000000000001";
    private const string FeedId = "20000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnqueueUsesLatestResolvedPolicyAndSkipsDisabledFeed()
    {
        FeedEntry entry = Entry();
        var jobs = new StubJobRepository();
        var service = CreateService(
            jobs,
            Catalog(autoSummary: true, autoTranslation: true),
            entry);

        await service.EnqueueAsync(FeedId, [entry], CancellationToken.None);

        Assert.Equal(1, jobs.EnqueueCalls);
        Assert.True(jobs.LastPolicy?.AutoSummaryEnabled);
        Assert.True(jobs.LastPolicy?.AutoTranslationEnabled);
        Assert.Equal("zh-Hans", jobs.LastPolicy?.TranslationTargetLanguage);

        service = CreateService(
            jobs,
            Catalog(autoSummary: true, autoTranslation: true, feedEnabled: false),
            entry);
        await service.EnqueueAsync(FeedId, [entry], CancellationToken.None);
        Assert.Equal(1, jobs.EnqueueCalls);
    }

    [Fact]
    public async Task BackgroundBatchGeneratesSummaryAndTranslationWithReaderCompatibleBlocks()
    {
        FeedEntry entry = Entry();
        var jobs = new StubJobRepository
        {
            Claimed =
            [
                Job("30000000-0000-4000-8000-000000000001", FeedAiAutomationTaskType.Summary, "und"),
                Job("30000000-0000-4000-8000-000000000002", FeedAiAutomationTaskType.Translation, "zh-Hans")
            ]
        };
        var summaries = new StubSummaryService();
        var translations = new StubTranslationService();
        var service = CreateService(
            jobs,
            Catalog(autoSummary: true, autoTranslation: true),
            entry,
            summaries,
            translations);

        int attempted = await service.ProcessBackgroundBatchAsync(CancellationToken.None);

        Assert.Equal(2, attempted);
        FeedAiSummaryInput summary = Assert.Single(summaries.Inputs);
        Assert.Equal(entry.ContentHash, summary.ContentHash);
        Assert.Contains("Hello world", summary.Content, StringComparison.Ordinal);
        FeedAiTranslationInput translation = Assert.Single(translations.Inputs);
        Assert.Equal("zh-Hans", translation.TargetLanguage);
        Assert.Collection(
            translation.Blocks,
            block =>
            {
                Assert.Equal(FeedAiTranslationBlockKind.Title, block.Kind);
                Assert.Equal(entry.Title, block.Text);
            },
            block =>
            {
                Assert.Equal(FeedAiTranslationBlockKind.Heading, block.Kind);
                Assert.Equal("Section", block.Text);
            },
            block =>
            {
                Assert.Equal(FeedAiTranslationBlockKind.Paragraph, block.Kind);
                Assert.Equal("Hello world", block.Text);
                Assert.Single(block.Links);
            });
        Assert.Equal(2, jobs.Completed.Count);
        Assert.All(jobs.Completed, completed =>
            Assert.Equal(FeedAiAutomationJobOutcome.Succeeded, completed.Outcome));
    }

    [Fact]
    public async Task PolicyDisabledBeforeClaimCompletionSkipsWithoutCallingAi()
    {
        FeedEntry entry = Entry();
        var jobs = new StubJobRepository
        {
            Claimed = [Job(
                "30000000-0000-4000-8000-000000000001",
                FeedAiAutomationTaskType.Summary,
                "und")]
        };
        var summaries = new StubSummaryService();
        var service = CreateService(
            jobs,
            Catalog(autoSummary: false, autoTranslation: false),
            entry,
            summaries);

        await service.ProcessBackgroundBatchAsync(CancellationToken.None);

        Assert.Empty(summaries.Inputs);
        CompletedJob completed = Assert.Single(jobs.Completed);
        Assert.Equal(FeedAiAutomationJobOutcome.Skipped, completed.Outcome);
        Assert.Equal("POLICY_DISABLED", completed.ErrorCode);
    }

    [Fact]
    public async Task DailyLimitDefersJobUntilNextUtcDayWithoutCallingAi()
    {
        FeedEntry entry = Entry();
        var jobs = new StubJobRepository
        {
            Claimed = [Job(
                "30000000-0000-4000-8000-000000000001",
                FeedAiAutomationTaskType.Summary,
                "und")],
            ReserveResult = false
        };
        var summaries = new StubSummaryService();
        var service = CreateService(
            jobs,
            Catalog(autoSummary: true, autoTranslation: false),
            entry,
            summaries);

        await service.ProcessBackgroundBatchAsync(CancellationToken.None);

        Assert.Empty(summaries.Inputs);
        RetryJob retry = Assert.Single(jobs.Retried);
        Assert.Equal("DAILY_ENTRY_LIMIT", retry.ErrorCode);
        Assert.Equal(new DateTimeOffset(2026, 7, 26, 0, 1, 0, TimeSpan.Zero), retry.NextAttemptAt);
    }

    [Fact]
    public async Task CancellationDuringAiCallReleasesDurableLease()
    {
        FeedEntry entry = Entry();
        var jobs = new StubJobRepository
        {
            Claimed = [Job(
                "30000000-0000-4000-8000-000000000001",
                FeedAiAutomationTaskType.Summary,
                "und")]
        };
        using var cancellation = new CancellationTokenSource();
        var summaries = new StubSummaryService { BeforeCall = cancellation.Cancel };
        var service = CreateService(
            jobs,
            Catalog(autoSummary: true, autoTranslation: false),
            entry,
            summaries);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ProcessBackgroundBatchAsync(cancellation.Token));

        Assert.Equal(1, jobs.ReleaseCalls);
        Assert.Empty(jobs.Completed);
        Assert.Empty(jobs.Retried);
    }

    private static FeedAiAutomationQueueService CreateService(
        StubJobRepository jobs,
        FeedCatalogSnapshot catalog,
        FeedEntry entry,
        StubSummaryService? summaries = null,
        StubTranslationService? translations = null) =>
        new(
            jobs,
            new StubCatalogRepository(catalog),
            new StubEntryRepository(entry),
            summaries ?? new StubSummaryService(),
            translations ?? new StubTranslationService(),
            new FixedTimeProvider(Now),
            FeedAiAutomationOptions.Default with
            {
                InitialDelay = TimeSpan.Zero,
                PollInterval = TimeSpan.FromMilliseconds(10)
            });

    private static FeedCatalogSnapshot Catalog(
        bool autoSummary,
        bool autoTranslation,
        bool feedEnabled = true)
    {
        FeedAiPolicy defaults = FeedAiPolicy.SafeDefaults with
        {
            AutoSummary = autoSummary
                ? FeedAiPolicySwitch.Enabled
                : FeedAiPolicySwitch.Disabled,
            AutoTranslation = autoTranslation
                ? FeedAiPolicySwitch.Enabled
                : FeedAiPolicySwitch.Disabled,
            MaxConcurrency = 2
        };
        return new(
            new(1, FeedCatalogScope.Active, Now, Now),
            [new(CategoryId, "Tech", "tech", 0, true, 1, Now, Now)],
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
        "entry-1",
        FeedId,
        "external-1",
        "https://news.example/articles/one",
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

    private static FeedAiAutomationJob Job(
        string id,
        FeedAiAutomationTaskType taskType,
        string targetLanguage) =>
        new(
            id,
            FeedId,
            "entry-1",
            new string('a', 64),
            taskType,
            targetLanguage,
            1,
            Guid.NewGuid().ToString("N"));

    private sealed class StubJobRepository : IFeedAiAutomationJobRepository
    {
        public IReadOnlyList<FeedAiAutomationJob> Claimed { get; init; } = [];
        public bool ReserveResult { get; init; } = true;
        public int EnqueueCalls { get; private set; }
        public ResolvedFeedAiPolicy? LastPolicy { get; private set; }
        public int ReleaseCalls { get; private set; }
        public ConcurrentBag<CompletedJob> Completed { get; } = [];
        public ConcurrentBag<RetryJob> Retried { get; } = [];

        public Task<int> EnqueueAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            ResolvedFeedAiPolicy policy,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            EnqueueCalls++;
            LastPolicy = policy;
            return Task.FromResult(entries.Count);
        }

        public Task<IReadOnlyList<FeedAiAutomationJob>> ClaimDueAsync(
            DateTimeOffset now,
            int maximumCount,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(Claimed);

        public Task<bool> TryReserveDailyEntryAsync(
            DateOnly usageDate,
            string feedId,
            string entryId,
            int dailyEntryLimit,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(ReserveResult);

        public Task CompleteAsync(
            FeedAiAutomationJob job,
            FeedAiAutomationJobOutcome outcome,
            string? errorCode,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            Completed.Add(new(job, outcome, errorCode));
            return Task.CompletedTask;
        }

        public Task ScheduleRetryAsync(
            FeedAiAutomationJob job,
            string errorCode,
            DateTimeOffset nextAttemptAt,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken)
        {
            Retried.Add(new(job, errorCode, nextAttemptAt));
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(
            FeedAiAutomationJob job,
            DateTimeOffset releasedAt,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubCatalogRepository(FeedCatalogSnapshot snapshot) : IFeedCatalogRepository
    {
        public Task ReplaceAsync(
            FeedCatalogSnapshot value,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogSnapshot?> GetCatalogAsync(
            FeedCatalogScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedCatalogSnapshot?>(snapshot);

        public Task MarkSynchronizedAsync(
            long expectedVersion,
            DateTimeOffset synchronizedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeedCatalogState> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot.State);
    }

    private sealed class StubEntryRepository(FeedEntry entry) : IFeedEntryRepository
    {
        public Task<FeedEntry?> GetByIdAsync(
            string entryId,
            CancellationToken cancellationToken) =>
            Task.FromResult<FeedEntry?>(entry.Id == entryId ? entry : null);

        public Task<FeedEntryPage> QueryAsync(
            FeedEntryQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FeedEntryPage([entry], 0, false));

        public Task UpsertAsync(
            string feedId,
            IReadOnlyList<FeedEntry> entries,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> DeleteExpiredUnprotectedAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class StubSummaryService : IFeedAiSummaryService
    {
        public ConcurrentBag<FeedAiSummaryInput> Inputs { get; } = [];
        public Action? BeforeCall { get; init; }

        public Task<FeedAiResult> SummarizeAsync(
            FeedAiSummaryInput input,
            CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            BeforeCall?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result(
                input.EntryId,
                input.ContentHash,
                FeedAiTaskType.Summary,
                "und"));
        }

        public Task<IReadOnlyList<FeedAiSummaryBatchItem>> SummarizeBatchAsync(
            IReadOnlyList<FeedAiSummaryInput> inputs,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubTranslationService : IFeedAiTranslationService
    {
        public ConcurrentBag<FeedAiTranslationInput> Inputs { get; } = [];

        public Task<FeedAiTranslationResult> TranslateAsync(
            FeedAiTranslationInput input,
            CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            FeedAiTranslatedBlock[] blocks = input.Blocks.Select(block => new FeedAiTranslatedBlock(
                block.Sequence,
                block.Kind,
                block.Text,
                $"translated:{block.Text}",
                block.ResourceUrl,
                block.HeadingLevel,
                block.Links)).ToArray();
            return Task.FromResult(new FeedAiTranslationResult(
                Result(
                    input.EntryId,
                    input.ContentHash,
                    FeedAiTaskType.Translation,
                    input.TargetLanguage),
                blocks));
        }
    }

    private static FeedAiResult Result(
        string entryId,
        string contentHash,
        FeedAiTaskType taskType,
        string targetLanguage) =>
        new(
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record CompletedJob(
        FeedAiAutomationJob Job,
        FeedAiAutomationJobOutcome Outcome,
        string? ErrorCode);

    private sealed record RetryJob(
        FeedAiAutomationJob Job,
        string ErrorCode,
        DateTimeOffset NextAttemptAt);
}
