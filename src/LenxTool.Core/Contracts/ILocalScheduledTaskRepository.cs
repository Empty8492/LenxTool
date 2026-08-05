using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface ILocalScheduledTaskRepository
{
    Task<LocalScheduledTask> SaveAsync(
        string id,
        LocalScheduleDefinition schedule,
        LocalScheduleMissedRunPolicy missedRunPolicy,
        bool isEnabled,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken);

    Task<LocalScheduledTask?> GetAsync(
        string id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalScheduledTask>> GetAllAsync(
        CancellationToken cancellationToken);

    Task<LocalScheduledTask?> SetEnabledAsync(
        string id,
        bool isEnabled,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken);
}
