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
/// 真实探针与导出器共享的连接授权边界；成功结果包含本次 DNS 解析后固定的地址。
/// </summary>
public interface IEntryIntegrationEndpointAuthorizer
{
    Task<EntryIntegrationProbeContext?> AuthorizeAsync(
        EntryIntegrationTarget target,
        EntryIntegrationPolicy policy,
        CancellationToken cancellationToken);
}

/// <summary>
/// 提供商专用探针；只能使用已经过策略和 DNS 校验的上下文，且不得返回第三方正文。
/// </summary>
public interface IEntryIntegrationHealthProbe
{
    EntryIntegrationKind Kind { get; }

    /// <summary>
    /// Webhook 的无 HMAC 配置只做公开能力探测；其余适配器默认必须先取得凭据。
    /// </summary>
    bool RequiresCredential => true;

    Task<EntryIntegrationProbeResult> ProbeAsync(
        EntryIntegrationProbeContext context,
        string credential,
        CancellationToken cancellationToken);
}
