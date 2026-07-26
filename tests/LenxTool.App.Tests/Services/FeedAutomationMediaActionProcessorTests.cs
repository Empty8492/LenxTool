using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationMediaActionProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task BatchClaimsOnlyMediaActionsAndCompletesTerminalResults()
    {
        var queue = new StubQueue
        {
            Claimed =
            [
                Lease(),
                Lease(suffix: '2'),
                Lease(suffix: '3'),
                Lease(suffix: '4')
            ]
        };
        var actions = new StubActions
        {
            Handler = (action, _) => Task.FromResult(
                action.EntryId switch
                {
                    "entry-2" =>
                        FeedAutomationMediaActionResult.EntryMissing,
                    "entry-3" =>
                        FeedAutomationMediaActionResult.FeedUnavailable,
                    "entry-4" =>
                        FeedAutomationMediaActionResult.NoSupportedMedia,
                    _ => FeedAutomationMediaActionResult.Completed
                })
        };
        FeedAutomationMediaActionProcessor processor =
            CreateProcessor(queue, actions);

        int attempted = await processor.ProcessBackgroundBatchAsync(
            CancellationToken.None);

        Assert.Equal(4, attempted);
        Assert.Equal(
            [FeedAutomationActionType.SendToMedia],
            queue.ClaimedTypes);
        Assert.Equal(1, queue.ClaimedMaximumCount);
        Assert.Contains(
            queue.Completed,
            item => item.Action.EntryId == "entry-1"
                && item.Outcome == FeedAutomationActionRunOutcome.Succeeded);
        Assert.Contains(
            queue.Completed,
            item => item.Action.EntryId == "entry-2"
                && item.ErrorCode == "ENTRY_MISSING");
        Assert.Contains(
            queue.Completed,
            item => item.Action.EntryId == "entry-3"
                && item.ErrorCode == "POLICY_DISABLED");
        Assert.Contains(
            queue.Completed,
            item => item.Action.EntryId == "entry-4"
                && item.ErrorCode == "MEDIA_UNAVAILABLE");
    }

    [Fact]
    public async Task RetryableFailureHonorsBoundedRetryAfter()
    {
        var queue = new StubQueue
        {
            Claimed = [Lease()]
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
        FeedAutomationMediaActionProcessor processor = CreateProcessor(
            queue,
            actions,
            Options() with
            {
                MaximumRetryDelay = TimeSpan.FromMinutes(30)
            });

        await processor.ProcessBackgroundBatchAsync(CancellationToken.None);

        RetriedAction retried = Assert.Single(queue.Retried);
        Assert.Equal("PROVIDERRATELIMITED", retried.ErrorCode);
        Assert.Equal(Now.AddMinutes(30), retried.NextAttemptAt);
        Assert.Empty(queue.Completed);
    }

    [Fact]
    public async Task RejectedMediaIsPermanentAndTimeoutIsRetryable()
    {
        var rejectedQueue = new StubQueue
        {
            Claimed = [Lease()]
        };
        var rejectedActions = new StubActions
        {
            Handler = (_, _) => throw new InvalidDataException("spoofed")
        };
        var timeoutQueue = new StubQueue
        {
            Claimed = [Lease()]
        };
        var timeoutActions = new StubActions
        {
            Handler = (_, _) => throw new TimeoutException("slow")
        };

        await CreateProcessor(
            rejectedQueue,
            rejectedActions).ProcessBackgroundBatchAsync(CancellationToken.None);
        await CreateProcessor(
            timeoutQueue,
            timeoutActions).ProcessBackgroundBatchAsync(CancellationToken.None);

        CompletedAction rejected = Assert.Single(rejectedQueue.Completed);
        Assert.Equal(FeedAutomationActionRunOutcome.Failed, rejected.Outcome);
        Assert.Equal("MEDIA_REJECTED", rejected.ErrorCode);
        Assert.Single(timeoutQueue.Retried);
        Assert.Equal("DOWNLOAD_TIMEOUT", timeoutQueue.Retried[0].ErrorCode);
    }

    [Fact]
    public async Task CancellationReleasesClaimedMediaLease()
    {
        var queue = new StubQueue
        {
            Claimed = [Lease()]
        };
        using var cancellation = new CancellationTokenSource();
        var actions = new StubActions
        {
            Handler = async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return FeedAutomationMediaActionResult.Completed;
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProcessor(queue, actions).ProcessBackgroundBatchAsync(
                cancellation.Token));

        Assert.Single(queue.Released);
        Assert.Empty(queue.Completed);
        Assert.Empty(queue.Retried);
    }

    private static FeedAutomationMediaActionProcessor CreateProcessor(
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

    private static FeedAutomationActionLease Lease(char suffix = '1') => new(
        new string(suffix, 64),
        $"entry-{suffix}",
        $"40000000-0000-4000-8000-00000000009{suffix}",
        1,
        100,
        0,
        FeedAutomationActionType.SendToMedia,
        10,
        null,
        1,
        new string(suffix, 32));

    private sealed class StubActions : IFeedAutomationMediaActionService
    {
        public required Func<
            FeedAutomationActionLease,
            CancellationToken,
            Task<FeedAutomationMediaActionResult>> Handler { get; init; }

        public Task<FeedAutomationMediaActionResult> ExecuteAsync(
            FeedAutomationActionLease action,
            CancellationToken cancellationToken) =>
            Handler(action, cancellationToken);
    }

    private sealed class StubQueue : IFeedAutomationActionQueueRepository
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
        public int ClaimedMaximumCount { get; private set; }
        public List<CompletedAction> Completed { get; } = [];
        public List<RetriedAction> Retried { get; } = [];
        public List<FeedAutomationActionLease> Released { get; } = [];

        public Task<IReadOnlyList<FeedAutomationActionLease>> ClaimDueAsync(
            DateTimeOffset now,
            IReadOnlyCollection<FeedAutomationActionType> actionTypes,
            int maximumCount,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            ClaimedTypes = actionTypes.ToArray();
            ClaimedMaximumCount = maximumCount;
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
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
