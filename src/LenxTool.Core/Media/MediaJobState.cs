using LenxTool.Core.Models;

namespace LenxTool.Core.Media;

public static class MediaJobState
{
    public static bool CanTransition(MediaJobStatus current, MediaJobStatus next) =>
        (current, next) switch
        {
            (MediaJobStatus.Queued, MediaJobStatus.Running or MediaJobStatus.Cancelled) => true,
            (MediaJobStatus.Running, MediaJobStatus.Completed or MediaJobStatus.Failed or MediaJobStatus.Cancelled) => true,
            (MediaJobStatus.Failed, MediaJobStatus.Queued) => true,
            _ => false
        };

    public static void EnsureTransition(MediaJobStatus current, MediaJobStatus next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"不允许从 {current} 转换到 {next}。");
        }
    }
}
