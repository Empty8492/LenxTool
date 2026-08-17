using System.IO;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationActionProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BatchClaimsOnlyLocalActionsAndCompletesTerminalResults()
    {
        var queue = new StubQueue
        {
            Claimed =
            [
                Lease(FeedAutomationActionType.MarkRead),
                Lease(FeedAutomationActionType.Hide, attemptCount: 1, suffix: '2')
            ]
        };
        var localActions = new StubLocalActions
        {
            Handler = (action, _) => Task.FromResult(
                action.Type == FeedAutomationActionType.Hide
                    ? FeedAutomationLocalActionResult.EntryMissing
                    : FeedAutomationLocalActionResult.Completed)
        };
        FeedAutomationActionProcessor processor = CreateProcessor(queue, localActions);

        int attempted = await processor.ProcessBackgroundBatchAsync(
            CancellationToken.None);

        Assert.Equal(2, attempted);
        Assert.Equal(
            [
                FeedAutomationActionType.AddTag,
                FeedAutomationActionType.Hide,
                FeedAutomationActionType.MarkRead
            ],
            queue.ClaimedTypes);
        Assert.Contains(
            queue.Completed,
            item => item.Action.Type == FeedAutomationActionType.MarkRead
                && item.Outcome == FeedAutomationActionRunOutcome.Succeeded
                && item.ErrorCode is null);
        Assert.Contains(
            queue.Completed,
            item => item.Action.Type == FeedAutomationActionType.Hide
                && item.Outcome == FeedAutomationActionRunOutcome.Failed
                && item.ErrorCode == "ENTRY_MISSING");
        Assert.Empty(queue.Retried);
    }

    [Fact]
    public async Task InvalidActionIsCompletedAsPermanentFailure()
    {
        var queue = new StubQueue
        {
            Claimed = [Lease(FeedAutomationActionType.AddTag)]
        };
        var localActions = new StubLocalActions
        {
            Handler = (_, _) => throw new InvalidDataException("Invalid payload.")
        };
        FeedAutomationActionProcessor processor = CreateProcessor(queue, localActions);

        await processor.ProcessBackgroundBatchAsync(CancellationToken.None);

        CompletedAction completed = Assert.Single(queue.Completed);
        Assert.Equal(FeedAutomationActionRunOutcome.Failed, completed.Outcome);
        Assert.Equal("INVALID_ACTION", completed.ErrorCode);
        Assert.Empty(queue.Retried);
    }

    [Fact]
    public async Task UnexpectedFailureRetriesThenBecomesPermanentAtAttemptLimit()
    {
        var queue = new StubQueue
        {
            Claimed =
            [
                Lease(FeedAutomationActionType.MarkRead),
                Lease(
                    FeedAutomationActionType.Hide,
                    attemptCount: 3,
                    suffix: '2')
            ]
        };
        var localActions = new StubLocalActions
        {
            Handler = (_, _) => throw new IOException("Temporary storage failure.")
        };
        FeedAutomationActionProcessor processor = CreateProcessor(
            queue,
            localActions,
            Options() with
            {
                MaximumAttempts = 3,
                BaseRetryDelay = TimeSpan.FromMinutes(2)
            });

        await processor.ProcessBackgroundBatchAsync(CancellationToken.None);

        RetriedAction retried = Assert.Single(queue.Retried);
        Assert.Equal("UNEXPECTED_ERROR", retried.ErrorCode);
        Assert.Equal(Now.AddMinutes(2), retried.NextAttemptAt);
        CompletedAction failed = Assert.Single(queue.Completed);
        Assert.Equal(FeedAutomationActionRunOutcome.Failed, failed.Outcome);
        Assert.Equal("UNEXPECTED_ERROR", failed.ErrorCode);
    }

    [Fact]
    public async Task RetryableAppFailureHonorsBoundedRetryAfter()
    {
        var queue = new StubQueue
        {
            Claimed = [Lease(FeedAutomationActionType.Hide)]
        };
        var localActions = new StubLocalActions
        {
            Handler = (_, _) => throw new AppException(
                new(
                    AppErrorCode.DatabaseCorrupted,
                    "Database unavailable",
                    "Try again.",
                    "Retry later.",
                    RetryAfter: TimeSpan.FromHours(3),
                    IsRetryable: true))
        };
        FeedAutomationActionProcessor processor = CreateProcessor(
            queue,
            localActions,
            Options() with
            {
                MaximumRetryDelay = TimeSpan.FromMinutes(30)
            });

        await processor.ProcessBackgroundBatchAsync(CancellationToken.None);

        RetriedAction retried = Assert.Single(queue.Retried);
        Assert.Equal("DATABASECORRUPTED", retried.ErrorCode);
        Assert.Equal(Now.AddMinutes(30), retried.NextAttemptAt);
        Assert.Empty(queue.Completed);
    }

    [Fact]
    public async Task CancellationReleasesClaimedLease()
    {
        var queue = new StubQueue
        {
            Claimed =
            [
                Lease(FeedAutomationActionType.MarkRead),
                Lease(
                    FeedAutomationActionType.Hide,
                    suffix: '2')
            ]
        };
        using var cancellation = new CancellationTokenSource();
        var localActions = new StubLocalActions
        {
            Handler = async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return FeedAutomationLocalActionResult.Completed;
            }
        };
        FeedAutomationActionProcessor processor = CreateProcessor(queue, localActions);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessBackgroundBatchAsync(cancellation.Token));

        Assert.Equal(2, queue.Released.Count);
        Assert.Empty(queue.Completed);
        Assert.Empty(queue.Retried);
    }

    private static FeedAutomationActionProcessor CreateProcessor(
        StubQueue queue,
        StubLocalActions localActions,
        FeedAutomationActionProcessorOptions? options = null) =>
        new(
            queue,
            localActions,
            new FixedTimeProvider(Now),
            options ?? Options());

    private static FeedAutomationActionProcessorOptions Options() =>
        FeedAutomationActionProcessorOptions.Default with
        {
            BatchSize = 10,
            MaximumConcurrency = 2,
            InitialDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(10),
            BaseRetryDelay = TimeSpan.FromMinutes(1),
            MaximumRetryDelay = TimeSpan.FromHours(1)
        };

    private static FeedAutomationActionLease Lease(
        FeedAutomationActionType type,
        int attemptCount = 1,
        char suffix = '1') =>
        new(
            new string(suffix, 64),
            $"entry-{suffix}",
            $"30000000-0000-4000-8000-00000000009{suffix}",
            1,
            100,
            0,
            type,
            10,
            type == FeedAutomationActionType.AddTag ? "AI" : null,
            attemptCount,
            new string(suffix, 32));

    private sealed class StubQueue : IFeedAutomationActionQueueRepository
    {
        public IReadOnlyList<FeedAutomationActionLease> Claimed { get; init; } = [];
        public FeedAutomationActionType[] ClaimedTypes { get; private set; } = [];
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
            return Task.FromResult(Claimed);
        }

        public Task CompleteAsync(
            FeedAutomationActionLease action,
            FeedAutomationActionRunOutcome outcome,
            string? errorCode,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            Completed.Add(new(action, outcome, errorCode, completedAt));
            return Task.CompletedTask;
        }

        public Task ScheduleRetryAsync(
            FeedAutomationActionLease action,
            string errorCode,
            DateTimeOffset nextAttemptAt,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken)
        {
            Retried.Add(new(
                action,
                errorCode,
                nextAttemptAt,
                failedAt));
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

    private sealed class StubLocalActions : IFeedAutomationLocalActionService
    {
        public required Func<
            FeedAutomationActionLease,
            CancellationToken,
            Task<FeedAutomationLocalActionResult>> Handler
        { get; init; }

        public Task<FeedAutomationLocalActionResult> ExecuteAsync(
            FeedAutomationActionLease action,
            CancellationToken cancellationToken) =>
            Handler(action, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record CompletedAction(
        FeedAutomationActionLease Action,
        FeedAutomationActionRunOutcome Outcome,
        string? ErrorCode,
        DateTimeOffset CompletedAt);

    private sealed record RetriedAction(
        FeedAutomationActionLease Action,
        string ErrorCode,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset FailedAt);
}
