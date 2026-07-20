using LenxTool.Core.Media;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Media;

public sealed class MediaJobStateTests
{
    [Theory]
    [InlineData(MediaJobStatus.Queued, MediaJobStatus.Running, true)]
    [InlineData(MediaJobStatus.Running, MediaJobStatus.Completed, true)]
    [InlineData(MediaJobStatus.Running, MediaJobStatus.Cancelled, true)]
    [InlineData(MediaJobStatus.Failed, MediaJobStatus.Queued, true)]
    [InlineData(MediaJobStatus.Completed, MediaJobStatus.Running, false)]
    [InlineData(MediaJobStatus.Cancelled, MediaJobStatus.Completed, false)]
    public void TransitionRulesProtectTerminalStates(
        MediaJobStatus current,
        MediaJobStatus next,
        bool expected)
    {
        Assert.Equal(expected, MediaJobState.CanTransition(current, next));
    }
}
