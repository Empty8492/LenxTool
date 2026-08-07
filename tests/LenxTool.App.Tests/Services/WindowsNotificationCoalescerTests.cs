using LenxTool.App.Services;
using LenxTool.Core.Models;

namespace LenxTool.App.Tests.Services;

public sealed class WindowsNotificationCoalescerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 8, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstNotificationIsImmediateAndFollowingItemsShareOneWindow()
    {
        var coalescer = new WindowsNotificationCoalescer();
        TimeSpan window = TimeSpan.FromMinutes(15);

        WindowsNotificationCoalescingDecision first =
            coalescer.Add(CreateNotification("1"), Now, window);
        WindowsNotificationCoalescingDecision second =
            coalescer.Add(
                CreateNotification("2"),
                Now.AddMinutes(2),
                window);
        WindowsNotificationCoalescingDecision third =
            coalescer.Add(
                CreateNotification("3"),
                Now.AddMinutes(14),
                window);

        Assert.Equal(
            WindowsNotificationCoalescingOutcome.ShowImmediately,
            first.Outcome);
        Assert.Equal(
            WindowsNotificationCoalescingOutcome.Deferred,
            second.Outcome);
        Assert.Equal(Now.Add(window), second.DueAt);
        Assert.Equal(second.DueAt, third.DueAt);
        Assert.Null(coalescer.TakeDue(Now.AddMinutes(14).AddSeconds(59)));

        WindowsNotificationBatch batch = Assert.IsType<
            WindowsNotificationBatch>(coalescer.TakeDue(Now.Add(window)));
        Assert.Equal(2, batch.Count);
        Assert.Equal("3", batch.Latest.Id);
    }

    [Fact]
    public void DisabledCoalescingShowsEveryNotificationImmediately()
    {
        var coalescer = new WindowsNotificationCoalescer();

        WindowsNotificationCoalescingDecision first = coalescer.Add(
            CreateNotification("1"),
            Now,
            TimeSpan.Zero);
        WindowsNotificationCoalescingDecision second = coalescer.Add(
            CreateNotification("2"),
            Now,
            TimeSpan.Zero);

        Assert.Equal(
            WindowsNotificationCoalescingOutcome.ShowImmediately,
            first.Outcome);
        Assert.Equal(
            WindowsNotificationCoalescingOutcome.ShowImmediately,
            second.Outcome);
        Assert.Null(coalescer.TakeDue(Now.AddHours(1)));
    }

    [Fact]
    public void ResetDropsPendingItemsAndRateHistory()
    {
        var coalescer = new WindowsNotificationCoalescer();
        TimeSpan window = TimeSpan.FromMinutes(15);
        coalescer.Add(CreateNotification("1"), Now, window);
        coalescer.Add(CreateNotification("2"), Now.AddMinutes(1), window);

        coalescer.Reset();
        WindowsNotificationCoalescingDecision next = coalescer.Add(
            CreateNotification("3"),
            Now.AddMinutes(2),
            window);

        Assert.Equal(
            WindowsNotificationCoalescingOutcome.ShowImmediately,
            next.Outcome);
        Assert.Null(coalescer.TakeDue(Now.AddHours(1)));
    }

    private static AppNotification CreateNotification(string id) =>
        new(
            id,
            "7f0a7aa7b7f2ee754c1f6337becc09d87885d985dfcba6e71bb69bee9c535b46",
            "feed-1",
            Guid.Empty.ToString("D"),
            1,
            $"标题 {id}",
            "来源",
            Now,
            ReadAt: null);
}
