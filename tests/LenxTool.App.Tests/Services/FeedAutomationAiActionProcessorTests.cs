using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationAiActionProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BatchClaimsOnlyAiActionsAndCompletesTerminalResults()
    {
        var queue = new StubQueue
        {
            Claimed =
            [
                Lease(FeedAutomationActionType.GenerateSummary),
                Lease(
                    FeedAutomationActionType.Translate,
                    value: "ja",
                    suffix: '2'),
                Lease(
                    FeedAutomationActionType.GenerateSummary,
                    suffix: '3')
            ]
        };
        var actions = new StubActions
        {
            Handler = (action, _) => Task.FromResult(
                action.EntryId switch
                {
                    "entry-2" =>
                        FeedAutomationAiActionResult.EntryMissing,
                    "entry-3" =>
                        FeedAutomationAiActionResult.FeedUnavailable,
                    _ => FeedAutomationAiActionResult.Completed
                })
        };
        FeedAutomationAiActionProcessor processor =
            CreateProcessor(queue, actions);

        int attempted = await processor.ProcessBackgroundBatchAsync(
            CancellationToken.None);

        Assert.Equal(3, attempted);
        Assert.Equal(
            [
                FeedAutomationActionType.GenerateSummary,
                FeedAutomationActionType.Translate
            ],
            queue.ClaimedTypes);
        Assert.Contains(
            queue.Completed,
            item => item.Action.Type
                    == FeedAutomationActionType.GenerateSummary
                && item.Outcome
                    == FeedAutomationActionRunOutcome.Succeeded);
        Assert.Contains(
            queue.Completed,
            item => item.Action.Type
                    == FeedAutomationActionType.Translate
                && item.Outcome
                    == FeedAutomationActionRunOutcome.Failed
                && item.ErrorCode == "ENTRY_MISSING");
        Assert.Contains(
            queue.Completed,
            item => item.Action.EntryId == "entry-3"
                && item.Outcome
                    == FeedAutomationActionRunOutcome.Failed
                && item.ErrorCode == "POLICY_DISABLED");
    }

    [Fact]
    public async Task RetryableAiFailureHonorsBoundedRetryAfter()
    {
        var queue = new StubQueue
        {
            Claimed =
            [
                Lease(
                    FeedAutomationActionType.GenerateSummary)
            ]
        };
        var actions = new StubActions
        {
            Handler = (_, _) => throw new AppException(
                new(
                    AppErrorCode.ProviderRateLimited,
                    "Rate limited",
                    "Try again.",
                    "Retry later.",
                    RetryAfter: TimeSpan.FromHours(3),
                    IsRetryable: true))
        };
        FeedAutomationAiActionProcessor processor =
            CreateProcessor(
                queue,
                actions,
                Options() with
                {
                    MaximumRetryDelay =
                        TimeSpan.FromMinutes(30)
                });

        await processor.ProcessBackgroundBatchAsync(
            CancellationToken.None);

        RetriedAction retried = Assert.Single(queue.Retried);
        Assert.Equal("PROVIDERRATELIMITED", retried.ErrorCode);
        Assert.Equal(
            Now.AddMinutes(30),
            retried.NextAttemptAt);
        Assert.Empty(queue.Completed);
    }

    [Fact]
    public async Task CancellationReleasesClaimedAiLeases()
    {
        var queue = new StubQueue
        {
            Claimed =
            [
                Lease(
                    FeedAutomationActionType.GenerateSummary),
                Lease(
                    FeedAutomationActionType.Translate,
                    value: "ja",
                    suffix: '2')
            ]
        };
        using var cancellation = new CancellationTokenSource();
        var actions = new StubActions
        {
            Handler = async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return FeedAutomationAiActionResult.Completed;
            }
        };
        FeedAutomationAiActionProcessor processor =
            CreateProcessor(queue, actions);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessBackgroundBatchAsync(
                cancellation.Token));

        Assert.Equal(2, queue.Released.Count);
        Assert.Empty(queue.Completed);
        Assert.Empty(queue.Retried);
    }

    private static FeedAutomationAiActionProcessor CreateProcessor(
        StubQueue queue,
        StubActions actions,
        FeedAutomationActionProcessorOptions? options = null) =>
        new(
            queue,
            actions,
            new FixedTimeProvider(Now),
            options ?? Options());

    private static FeedAutomationActionProcessorOptions Options() =>
        FeedAutomationActionProcessorOptions.Default with
        {
            BatchSize = 10,
            MaximumAttempts = 3,
            InitialDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(10),
            BaseRetryDelay = TimeSpan.FromMinutes(1),
            MaximumRetryDelay = TimeSpan.FromHours(1)
        };

    private static FeedAutomationActionLease Lease(
        FeedAutomationActionType type,
        string? value = null,
        char suffix = '1') => new(
        new string(suffix, 64),
        $"entry-{suffix}",
        $"40000000-0000-4000-8000-00000000009{suffix}",
        1,
        100,
        0,
        type,
        10,
        value,
        1,
        new string(suffix, 32));

    private sealed class StubActions
        : IFeedAutomationAiActionService
    {
        public required Func<
            FeedAutomationActionLease,
            CancellationToken,
            Task<FeedAutomationAiActionResult>> Handler { get; init; }

        public Task<FeedAutomationAiActionResult> ExecuteAsync(
            FeedAutomationActionLease action,
            CancellationToken cancellationToken) =>
            Handler(action, cancellationToken);
    }

    private sealed class StubQueue
        : IFeedAutomationActionQueueRepository
    {
        public IReadOnlyList<FeedAutomationActionLease> Claimed
        {
            get;
            init;
        } = [];
        public FeedAutomationActionType[] ClaimedTypes
        {
            get;
            private set;
        } = [];
        public List<CompletedAction> Completed { get; } = [];
        public List<RetriedAction> Retried { get; } = [];
        public List<FeedAutomationActionLease> Released { get; } = [];

        public Task<IReadOnlyList<FeedAutomationActionLease>>
            ClaimDueAsync(
                DateTimeOffset now,
                IReadOnlyCollection<FeedAutomationActionType>
                    actionTypes,
                int maximumCount,
                TimeSpan leaseDuration,
                CancellationToken cancellationToken)
        {
            ClaimedTypes = actionTypes.ToArray();
            return Task.FromResult(Claimed);
        }

        public Task CompleteAsync(
            FeedAutomationActionLease action,
            FeedAutomationActionRunOutcome outcome,
            string? errorCode,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            Completed.Add(new(action, outcome, errorCode));
            return Task.CompletedTask;
        }

        public Task ScheduleRetryAsync(
            FeedAutomationActionLease action,
            string errorCode,
            DateTimeOffset nextAttemptAt,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken)
        {
            Retried.Add(new(action, errorCode, nextAttemptAt));
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(
            FeedAutomationActionLease action,
            DateTimeOffset releasedAt,
            CancellationToken cancellationToken)
        {
            Released.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record CompletedAction(
        FeedAutomationActionLease Action,
        FeedAutomationActionRunOutcome Outcome,
        string? ErrorCode);

    private sealed record RetriedAction(
        FeedAutomationActionLease Action,
        string ErrorCode,
        DateTimeOffset NextAttemptAt);
}
