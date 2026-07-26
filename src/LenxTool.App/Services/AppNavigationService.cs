namespace LenxTool.App.Services;

public sealed record AppNavigationRequest(
    string RouteId,
    string EntityType,
    string EntityId);

public interface IAppNavigationService
{
    Task NavigateAsync(
        AppNavigationRequest request,
        CancellationToken cancellationToken);
}

public interface IEntityNavigationAware
{
    Task OpenEntityAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken);
}

public sealed class AppNavigationService : IAppNavigationService
{
    private Func<AppNavigationRequest, CancellationToken, Task>? _handler;

    internal void Attach(
        Func<AppNavigationRequest, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_handler is not null)
        {
            throw new InvalidOperationException(
                "应用导航处理器已经附加。");
        }
        _handler = handler;
    }

    public Task NavigateAsync(
        AppNavigationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_handler is null)
        {
            throw new InvalidOperationException(
                "应用导航尚未完成初始化。");
        }
        return _handler(request, cancellationToken);
    }
}
