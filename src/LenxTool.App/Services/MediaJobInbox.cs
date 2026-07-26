using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public interface IMediaJobInbox
{
    event Action<MediaJob>? JobQueued;

    void PublishQueued(MediaJob job);
}

public sealed class MediaJobInbox : IMediaJobInbox
{
    public event Action<MediaJob>? JobQueued;

    public void PublishQueued(MediaJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Delegate[] handlers = JobQueued?.GetInvocationList() ?? [];
        foreach (Action<MediaJob> handler in handlers.Cast<Action<MediaJob>>())
        {
            try
            {
                handler(job);
            }
            catch
            {
                // A UI notification must never roll back a durable delivery.
            }
        }
    }
}
