using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationNotificationActionProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BatchClaimsOnlyNotifyActionsAndCompletesTerminalResults()
    {
        var queue = new StubQueue
        {
            Claimed =
            [
                Lease(),
                Lease('2'),
                Lease('3')
            ]
        };
        var actions = new StubActions
        {
            Handler = (action, _) => Task.FromResult(
                action.EntryId switch
                {
                    "entry-2" =>
                        FeedAutomationNotificationActionResult.EntryMissing,
                    "entry-3" =>
                        FeedAutomationNotificationActionResult.FeedUnavailable,
                    _ => FeedAutomationNotificationActionResult.Completed
                })
        };

        int attempted = await CreateProcessor(queue, actions)
            .ProcessBackgroundBatchAsync(CancellationToken.None);

        Assert.Equal(3, attempted);
        Assert.Equal(
            [FeedAutomationActionType.Notify],
            queue.ClaimedTypes);
        Assert.Equal(10, queue.ClaimedMaximumCount);
        Assert.Contains(
            queue.Completed,
            item => item.Action.EntryId == "entry-1" &&
                item.Outcome == FeedAutomationActionRunOutcome.Succeeded);
        Assert.Contains(
            queue.Completed,
            item => item.Action.EntryId == "entry-2" &&
                item.ErrorCode == "ENTRY_MISSING");
        Assert.Contains(
            queue.Completed,
            item => item.Action.EntryId == "entry-3" &&
                item.ErrorCode == "POLICY_DISABLED");
    }

    [Fact]
    public async Task InvalidActionIsPermanentAndRetryableFailureIsBounded()
    {
        var invalidQueue = new StubQueue
        {
            Claimed = [Lease()]
        };
        var invalidActions = new StubActions
        {
            Handler = (_, _) =>
                throw new ArgumentException("invalid")
        };
        await CreateProcessor(invalidQueue, invalidActions)
            .ProcessBackgroundBatchAsync(CancellationToken.None);

        CompletedAction invalid = Assert.Single(invalidQueue.Completed);
        Assert.Equal(FeedAutomationActionRunOutcome.Failed, invalid.Outcome);
        Assert.Equal("INVALID_ACTION", invalid.ErrorCode);

        var retryQueue = new StubQueue
        {
            Claimed = [Lease()]
        };
        var retryActions = new StubActions
        {
            Handler = (_, _) => throw new AppException(
                new(
                    AppErrorCode.ProviderUnavailable,
                    "Unavailable",
                    "Try again.",
                    "Retry later.",
                    RetryAfter: TimeSpan.FromHours(3),
                    IsRetryable: true))
        };
        await CreateProcessor(
            retryQueue,
            retryActions,
            Options() with
            {
                MaximumRetryDelay = TimeSpan.FromMinutes(30)
            }).ProcessBackgroundBatchAsync(CancellationToken.None);

        RetriedAction retry = Assert.Single(retryQueue.Retried);
        Assert.Equal("PROVIDERUNAVAILABLE", retry.ErrorCode);
        Assert.Equal(Now.AddMinutes(30), retry.NextAttemptAt);
    }

    [Fact]
    public async Task CancellationReleasesClaimedNotifyLeases()
    {
        var queue = new StubQueue
        {
            Claimed = [Lease(), Lease('2')]
        };
        using var cancellation = new CancellationTokenSource();
        var actions = new StubActions
        {
            Handler = async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return FeedAutomationNotificationActionResult.Completed;
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProcessor(queue, actions).ProcessBackgroundBatchAsync(
                cancellation.Token));

        Assert.Equal(2, queue.Released.Count);
        Assert.Empty(queue.Completed);
        Assert.Empty(queue.Retried);
    }

    private static FeedAutomationNotificationActionProcessor CreateProcessor(
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
            MaximumConcurrency = 4,
            MaximumAttempts = 3,
            InitialDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(10),
            BaseRetryDelay = TimeSpan.FromMinutes(1),
            MaximumRetryDelay = TimeSpan.FromHours(1)
        };

    private static FeedAutomationActionLease Lease(char suffix = '1') => new(
        new string(suffix, 64),
        $"entry-{suffix}",
        $"40000000-0000-4000-8000-00000000008{suffix}",
        1,
        100,
        0,
        FeedAutomationActionType.Notify,
        10,
        null,
        1,
        new string(suffix, 32));

    private sealed class StubActions
        : IFeedAutomationNotificationActionService
    {
        public required Func<
            FeedAutomationActionLease,
            CancellationToken,
            Task<FeedAutomationNotificationActionResult>> Handler
        {
            get;
            init;
        }

        public Task<FeedAutomationNotificationActionResult> ExecuteAsync(
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
