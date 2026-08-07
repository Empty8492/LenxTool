using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public enum WindowsNotificationCoalescingOutcome
{
    ShowImmediately,
    Deferred
}

public sealed record WindowsNotificationCoalescingDecision(
    WindowsNotificationCoalescingOutcome Outcome,
    DateTimeOffset? DueAt);

public sealed record WindowsNotificationBatch(
    AppNotification Latest,
    int Count);

public sealed class WindowsNotificationCoalescer
{
    private DateTimeOffset? _lastShownAt;
    private DateTimeOffset? _dueAt;
    private AppNotification? _latest;
    private int _pendingCount;

    public WindowsNotificationCoalescingDecision Add(
        AppNotification notification,
        DateTimeOffset now,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (window < TimeSpan.Zero || window > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        if (window == TimeSpan.Zero || _lastShownAt is null)
        {
            MarkShown(now);
            return new(
                WindowsNotificationCoalescingOutcome.ShowImmediately,
                DueAt: null);
        }

        DateTimeOffset windowEnd = _lastShownAt.Value.Add(window);
        if (now >= windowEnd && _pendingCount == 0)
        {
            MarkShown(now);
            return new(
                WindowsNotificationCoalescingOutcome.ShowImmediately,
                DueAt: null);
        }

        _latest = notification;
        _pendingCount++;
        _dueAt = windowEnd > now ? windowEnd : now;
        return new(
            WindowsNotificationCoalescingOutcome.Deferred,
            _dueAt);
    }

    public WindowsNotificationBatch? TakeDue(DateTimeOffset now)
    {
        if (_pendingCount == 0 || _latest is null ||
            _dueAt is null || now < _dueAt.Value)
        {
            return null;
        }

        var batch = new WindowsNotificationBatch(
            _latest,
            _pendingCount);
        MarkShown(now);
        return batch;
    }

    public void Reset()
    {
        _lastShownAt = null;
        ClearPending();
    }

    private void MarkShown(DateTimeOffset now)
    {
        _lastShownAt = now;
        ClearPending();
    }

    private void ClearPending()
    {
        _dueAt = null;
        _latest = null;
        _pendingCount = 0;
    }
}
