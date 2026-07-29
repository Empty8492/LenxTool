using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

/// <summary>
/// 在共享策略和本机安全边界内返回封闭的目标健康状态。
/// </summary>
public interface IEntryIntegrationHealthService
{
    Task<EntryIntegrationHealthResult> CheckAsync(
        EntryIntegrationTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// 提供商专用探针；只能使用已经过策略和 DNS 校验的上下文，且不得返回第三方正文。
/// </summary>
public interface IEntryIntegrationHealthProbe
{
    EntryIntegrationKind Kind { get; }

    Task<EntryIntegrationProbeResult> ProbeAsync(
        EntryIntegrationProbeContext context,
        string credential,
        CancellationToken cancellationToken);
}
