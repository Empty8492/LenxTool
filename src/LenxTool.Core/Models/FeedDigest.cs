namespace LenxTool.Core.Models;

using System.IO;
using System.Text.Json;

/// <summary>
/// 本地聚合摘要只提供日、周两个稳定处理器；月度计划仍属于通用调度能力，
/// 不能在没有产品输入语义时自动升级为摘要任务。
/// </summary>
public enum FeedDigestPeriod
{
    Daily,
    Weekly
}

/// <summary>
/// 摘要范围始终叠加 ACTIVE 目录限制。Feed、分类和关键词均为空时表示
/// 所有已启用订阅；这些字段不会上传到 Worker/D1。
/// </summary>
public sealed record FeedDigestScope(
    string? FeedId,
    string? CategoryId,
    string? SearchText)
{
    public static FeedDigestScope AllActive { get; } = new(null, null, null);

    public static FeedDigestScope Normalize(FeedDigestScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(
            NormalizeGuid(scope.FeedId, nameof(FeedId)),
            NormalizeGuid(scope.CategoryId, nameof(CategoryId)),
            NormalizeSearch(scope.SearchText));
    }

    private static string? NormalizeGuid(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }
        if (!Guid.TryParseExact(value, "D", out Guid parsed))
        {
            throw new ArgumentException(
                "摘要范围标识必须是规范 GUID。",
                parameterName);
        }
        return parsed.ToString("D");
    }

    private static string? NormalizeSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string normalized = value.Trim();
        if (normalized.Length > 200 || normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return normalized;
    }
}

public sealed record FeedDigestWindow(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

/// <summary>
/// 摘要的所有消费预算集中在一个封闭配置中，使模型调用、缓存身份和测试
/// 使用同一组上限，避免 UI 或后台各自裁剪后产生不同账单身份。
/// </summary>
public sealed record FeedDigestOptions(
    string Model,
    string PromptVersion,
    int MaximumEntries,
    int MaximumCandidateEntries,
    int MaximumCharactersPerEntry,
    int MaximumSourceCharacters,
    int MaximumResponseBytes,
    int MaximumOutputTokens,
    int MaximumReportCharacters)
{
    public static FeedDigestOptions Default { get; } = new(
        "deepseek-v4-flash",
        "feed-digest-v1",
        40,
        200,
        1_200,
        16_000,
        2_000_000,
        1_200,
        8_000);
}

/// <summary>
/// 交给模型的确定性摘要计划。ReportId 已包含范围、窗口、内容哈希、模型
/// 和 prompt 版本；相同窗口重放可先查本地报告而不再次计费。
/// </summary>
public sealed record FeedDigestPlan(
    string ReportId,
    string ScheduleId,
    FeedDigestPeriod Period,
    FeedDigestScope Scope,
    FeedDigestWindow Window,
    int EntryCount,
    string ContentHash,
    string Title,
    string SourceContent);

public sealed record FeedDigestScheduleConfiguration(
    FeedDigestPeriod Period,
    TimeOnly LocalTime,
    DayOfWeek? WeeklyDay,
    string TimeZoneId,
    bool IsEnabled,
    FeedDigestScope Scope);

public sealed record FeedDigestScheduleState(
    FeedDigestPeriod Period,
    TimeOnly LocalTime,
    DayOfWeek? WeeklyDay,
    string TimeZoneId,
    bool IsEnabled,
    DateTimeOffset? NextRunAtUtc,
    FeedDigestScope Scope);

public static class FeedDigestScheduleIds
{
    // ID 一旦发布就成为 local_scheduled_tasks 与 local_schedule_runs 的持久业务键，
    // 后续只能沿用，不能按版本或安装随机生成。
    public const string Daily = "5ac8ce8b-6e87-4a4f-bf25-bc87741c40d6";
    public const string Weekly = "68ee308c-700d-4b75-bfd7-641f9d0dd752";

    public static string For(FeedDigestPeriod period) =>
        period switch
        {
            FeedDigestPeriod.Daily => Daily,
            FeedDigestPeriod.Weekly => Weekly,
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };
}

/// <summary>
/// 与计划行一同持久化的版本化范围负载。反序列化失败时拒绝执行，不能把
/// 损坏或未知版本静默解释为“全部订阅”。
/// </summary>
public static class FeedDigestScopePayload
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string Serialize(FeedDigestScope scope)
    {
        FeedDigestScope normalized = FeedDigestScope.Normalize(scope);
        return JsonSerializer.Serialize(
            new ScopeDocument(
                1,
                normalized.FeedId,
                normalized.CategoryId,
                normalized.SearchText),
            JsonOptions);
    }

    public static FeedDigestScope Deserialize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 4_096)
        {
            throw new InvalidDataException(
                "本地摘要计划缺少有效范围负载。");
        }
        try
        {
            ScopeDocument document = JsonSerializer.Deserialize<ScopeDocument>(
                    payload,
                    JsonOptions)
                ?? throw new JsonException("摘要范围为空。");
            if (document.Version != 1)
            {
                throw new InvalidDataException(
                    "本地摘要范围版本不受支持。");
            }
            return FeedDigestScope.Normalize(new(
                document.FeedId,
                document.CategoryId,
                document.SearchText));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                "本地摘要范围负载无法读取。",
                exception);
        }
    }

    private sealed record ScopeDocument(
        int Version,
        string? FeedId,
        string? CategoryId,
        string? SearchText);
}

public enum FeedDigestExecutionBeginResult
{
    Started,
    SuppressedUncertainPriorAttempt,
    AlreadyCompleted
}
