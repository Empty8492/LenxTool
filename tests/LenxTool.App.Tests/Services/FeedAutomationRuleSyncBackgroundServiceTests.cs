using LenxTool.App.Services;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LenxTool.App.Tests.Services;

public sealed class FeedAutomationRuleSyncBackgroundServiceTests
{
    [Fact]
    public async Task LoginSignalsImmediateSynchronization()
    {
        var account = new FakeAccountSessionService();
        var sync = new FakeRuleSyncService();
        using var background = new FeedAutomationRuleSyncBackgroundService(
            account,
            sync,
            new(
                SynchronizationInterval: TimeSpan.FromHours(1),
                RetryInterval: TimeSpan.FromMilliseconds(20)),
            NullLogger<FeedAutomationRuleSyncBackgroundService>.Instance);
        await background.StartAsync(CancellationToken.None);

        account.SignIn();
        await WaitUntilAsync(() => sync.CallCount >= 1);

        await background.StopAsync(CancellationToken.None);
        Assert.True(sync.CallCount >= 1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeAccountSessionService
        : IAccountSessionService
    {
        public bool IsConfigured => true;
        public AccountSessionSnapshot Current { get; private set; } =
            AccountSessionSnapshot.SignedOut;

        public event EventHandler<AccountSessionChangedEventArgs>?
            SessionChanged;

        public void SignIn()
        {
            Current = new(
                AccountSessionStatus.SignedIn,
                new(
                    "10000000-0000-4000-8000-000000000001",
                    "reader",
                    AccountRole.User));
            SessionChanged?.Invoke(this, new(Current));
        }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task LoginAsync(
            string username,
            string password,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RefreshAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task LogoutAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeRuleSyncService
        : IFeedAutomationRuleSyncService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<FeedAutomationRuleSyncResult> SyncAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new FeedAutomationRuleSyncResult(
                FeedAutomationRuleSyncOutcome.Unchanged,
                0,
                DateTimeOffset.UtcNow));
        }
    }
}
