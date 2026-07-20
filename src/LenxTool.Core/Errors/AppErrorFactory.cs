using System.Net;

namespace LenxTool.Core.Errors;

public static class AppErrorFactory
{
    public static AppError FromHttp(
        HttpStatusCode statusCode,
        string? provider = null,
        string? requestId = null,
        string? technicalDetails = null,
        TimeSpan? retryAfter = null,
        string? limit = null,
        string? used = null) =>
        statusCode switch
        {
            HttpStatusCode.BadRequest => new(
                AppErrorCode.InvalidRequest, "请求内容有误",
                "服务商无法处理当前请求，可能是文件格式、模型或参数不受支持。",
                "请检查输入文件和模型设置后重试。", technicalDetails, provider, requestId),
            HttpStatusCode.Unauthorized => new(
                AppErrorCode.CredentialsInvalid, "认证失败",
                "API Key 或登录凭据无效、已过期或已被撤销。",
                "请打开设置更新凭据；共享额度用户可尝试重新登录。", technicalDetails, provider, requestId),
            HttpStatusCode.Forbidden => new(
                AppErrorCode.AccessDenied, "没有访问权限",
                "当前账号或密钥无权使用该模型或功能。",
                "请检查账号状态、模型权限或联系管理员。", technicalDetails, provider, requestId),
            HttpStatusCode.TooManyRequests => CreateRateLimitError(
                provider, requestId, technicalDetails, retryAfter, limit, used),
            >= HttpStatusCode.InternalServerError => new(
                AppErrorCode.ProviderUnavailable, "服务暂时不可用",
                "服务商当前发生故障，已保留本地任务信息。",
                "请稍后重试；语音任务也可以切换到本地 Whisper。", technicalDetails, provider,
                requestId, retryAfter, true),
            _ => new(
                AppErrorCode.Unknown, "请求未完成",
                $"服务返回了未预期的状态码 {(int)statusCode}。",
                "请重试；若持续发生，可复制详情并打开日志。", technicalDetails, provider,
                requestId, retryAfter, true)
        };

    public static AppError FromTimeout(string? provider = null, string? requestId = null) =>
        new(
            AppErrorCode.Timeout, "服务响应超时",
            "网络连接正常，但服务在限定时间内没有完成响应。",
            "请重试；长媒体任务可切换本地 Whisper。",
            Provider: provider, RequestId: requestId, IsRetryable: true);

    public static AppError FromNetwork(string? provider = null, string? requestId = null) =>
        new(
            AppErrorCode.NetworkUnavailable, "网络连接中断",
            "当前无法连接远端服务。资讯中心将优先显示本地缓存。",
            "请检查网络或代理设置后重试。",
            Provider: provider, RequestId: requestId, IsRetryable: true);

    private static AppError CreateRateLimitError(
        string? provider,
        string? requestId,
        string? technicalDetails,
        TimeSpan? retryAfter,
        string? limit,
        string? used)
    {
        string quota = string.IsNullOrWhiteSpace(limit) && string.IsNullOrWhiteSpace(used)
            ? string.Empty
            : $" 限额：{limit ?? "未知"}；已用：{used ?? "未知"}。";

        return new(
            AppErrorCode.ProviderRateLimited, "请求过于频繁",
            $"服务商已触发频率或额度限制。{quota}",
            retryAfter is null
                ? "请稍后重试，或切换自备 Key/本地 Whisper。"
                : $"请在约 {Math.Ceiling(retryAfter.Value.TotalSeconds)} 秒后重试，或切换自备 Key/本地 Whisper。",
            technicalDetails, provider, requestId, retryAfter, true);
    }
}
