using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record FeedHealthItem(
    FeedCatalogItem Feed,
    FeedFetchState? State)
{
    public string StatusLabel =>
        State is null
            ? "未抓取"
            : State.ConsecutiveFailures > 0
                ? $"失败 · 连续 {State.ConsecutiveFailures} 次"
                : State.LastSuccessAt is not null
                    ? "健康"
                    : "等待首次抓取";

    public string ErrorLabel => MapError(State?.ErrorCode);

    public string LastSuccessText => State?.LastSuccessAt is DateTimeOffset value
        ? $"最后成功 {value.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "从未成功";

    public string LastFailureText => State?.LastFailureAt is DateTimeOffset value
        ? $"最后失败 {value.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "从未失败";

    public string NextRetryText => State?.NextFetchAt is DateTimeOffset value
        ? $"下次 {value.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "未排期";

    public bool CanRetry => Feed.IsEnabled;

    private static string MapError(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "无";
        if (code.StartsWith("http_", StringComparison.Ordinal)
            && int.TryParse(code[5..], out int status))
        {
            return $"HTTP {status}";
        }

        return code switch
        {
            "network" => "网络错误",
            "timeout" => "请求超时",
            "invalid_feed" => "Feed 格式错误",
            "invalid_response" => "响应不可用",
            "unsafe_endpoint" => "地址安全校验失败",
            "storage" => "本地存储错误",
            _ => "抓取失败"
        };
    }
}
