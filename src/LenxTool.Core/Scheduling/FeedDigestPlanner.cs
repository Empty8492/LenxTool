using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LenxTool.Core.Models;

namespace LenxTool.Core.Scheduling;

/// <summary>
/// 纯逻辑地计算摘要窗口、去重和消费预算。外部 Feed 文本只会成为有界 DATA，
/// 不参与计划 ID、文件路径或任何可执行指令。
/// </summary>
public static class FeedDigestPlanner
{
    public static FeedDigestWindow GetWindow(
        FeedDigestPeriod period,
        LocalScheduleDefinition schedule,
        DateTimeOffset scheduledForUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ValidatePeriodSchedule(period, schedule);
        DateTimeOffset normalizedEnd = scheduledForUtc.ToUniversalTime();
        int searchDays = period is FeedDigestPeriod.Daily ? 3 : 21;
        DateTimeOffset? candidate = LocalScheduleCalculator.GetNextOccurrenceUtc(
            schedule,
            normalizedEnd.AddDays(-searchDays));
        DateTimeOffset? previous = null;
        for (int iteration = 0; iteration < 8 && candidate is not null; iteration++)
        {
            if (candidate.Value >= normalizedEnd)
            {
                if (candidate.Value != normalizedEnd || previous is null)
                {
                    throw new ArgumentException(
                        "执行窗口与当前摘要计划不一致。",
                        nameof(scheduledForUtc));
                }
                return new(previous.Value, normalizedEnd);
            }

            previous = candidate;
            candidate = LocalScheduleCalculator.GetNextOccurrenceUtc(
                schedule,
                candidate.Value);
        }

        throw new ArgumentException(
            "无法从摘要计划解析前一个本地日历窗口。",
            nameof(scheduledForUtc));
    }

    public static FeedDigestPlan? CreatePlan(
        FeedDigestPeriod period,
        string scheduleId,
        FeedDigestScope scope,
        FeedDigestWindow window,
        IReadOnlyList<FeedEntry> entries,
        FeedDigestOptions options,
        string? title = null)
    {
        ValidatePeriod(period);
        ValidateScheduleId(scheduleId, period);
        FeedDigestScope normalizedScope = FeedDigestScope.Normalize(scope);
        ValidateWindow(window);
        ValidateOptions(options);
        ArgumentNullException.ThrowIfNull(entries);

        IReadOnlyList<PlannedEntry> selected = entries
            .Select(ToPlannedEntry)
            .Where(entry => entry.TimestampUtc >= window.StartUtc
                            && entry.TimestampUtc < window.EndUtc)
            .OrderByDescending(entry => entry.TimestampUtc)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .DistinctBy(entry => entry.DeduplicationKey, StringComparer.Ordinal)
            .Take(options.MaximumEntries)
            .ToArray();
        if (selected.Count == 0)
        {
            return null;
        }

        SourceBuild source = BuildSource(selected, options);
        // 账单身份必须只取决于真正进入模型的数据。否则被总预算截掉的尾部条目
        // 也会改变缓存键，造成相同 prompt 被重复计费。
        string contentHash = Sha256(source.Content);
        string reportIdentity = Canonicalize(
            ((int)period).ToString(CultureInfo.InvariantCulture),
            scheduleId,
            normalizedScope.FeedId ?? string.Empty,
            normalizedScope.CategoryId ?? string.Empty,
            normalizedScope.SearchText ?? string.Empty,
            Format(window.StartUtc),
            Format(window.EndUtc),
            contentHash,
            options.Model,
            options.PromptVersion);
        return new(
            $"feed-digest-{Sha256(reportIdentity)}",
            scheduleId,
            period,
            normalizedScope,
            window,
            source.EntryCount,
            contentHash,
            NormalizeTitle(title, period, window),
            source.Content);
    }

    private static PlannedEntry ToPlannedEntry(FeedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string id = ValidateText(entry.Id, nameof(entry.Id), 256);
        string feedId = ValidateText(entry.FeedId, nameof(entry.FeedId), 128);
        string? normalizedUrl = NormalizeOptionalText(
            entry.NormalizedUrl,
            2_048);
        string contentHash = NormalizeOptionalText(entry.ContentHash, 128)
            ?? string.Empty;
        string deduplicationKey = normalizedUrl is not null
            ? $"url:{normalizedUrl}"
            : !string.IsNullOrWhiteSpace(contentHash)
                ? $"hash:{contentHash.ToLowerInvariant()}"
                : $"id:{id}";
        string title = NormalizeForPrompt(entry.Title, 300);
        string content = NormalizeForPrompt(
            string.IsNullOrWhiteSpace(entry.Summary)
                ? entry.SanitizedContent
                : entry.Summary,
            2_000_000);
        DateTimeOffset timestamp = (entry.PublishedAt
                                    ?? entry.UpdatedAt
                                    ?? entry.FetchedAt)
            .ToUniversalTime();
        return new(
            id,
            feedId,
            normalizedUrl,
            title,
            content,
            timestamp,
            deduplicationKey);
    }

    private static SourceBuild BuildSource(
        IReadOnlyList<PlannedEntry> entries,
        FeedDigestOptions options)
    {
        var builder = new StringBuilder(
            Math.Min(options.MaximumSourceCharacters, 16_384));
        int entryCount = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            PlannedEntry entry = entries[index];
            string boundedContent = Take(
                entry.Content,
                options.MaximumCharactersPerEntry);
            string block = string.Join(
                '\n',
                $"[{index + 1}]",
                $"标题：{entry.Title}",
                $"时间：{Format(entry.TimestampUtc)}",
                $"Feed：{entry.FeedId}",
                $"链接：{entry.NormalizedUrl ?? "（无）"}",
                $"摘要：{boundedContent}",
                string.Empty);
            int remaining = options.MaximumSourceCharacters - builder.Length;
            if (remaining <= 0)
            {
                break;
            }
            string appended = Take(block, remaining);
            builder.Append(appended);
            entryCount++;
            if (appended.Length < block.Length)
            {
                break;
            }
        }
        return new(builder.ToString(), entryCount);
    }

    private static string Canonicalize(params string[] values) =>
        string.Concat(values.Select(value =>
            $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}"));

    private static string? NormalizeOptionalText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string normalized = value.Trim();
        if (normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return normalized;
    }

    private static string ValidateText(
        string value,
        string parameterName,
        int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return value;
    }

    private static string NormalizeTitle(
        string? title,
        FeedDigestPeriod period,
        FeedDigestWindow window)
    {
        string fallback = period switch
        {
            FeedDigestPeriod.Daily =>
                $"每日订阅摘要 · {window.EndUtc:yyyy-MM-dd} UTC",
            FeedDigestPeriod.Weekly =>
                $"每周订阅摘要 · {window.EndUtc:yyyy-MM-dd} UTC",
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };
        return ValidateText(
            string.IsNullOrWhiteSpace(title) ? fallback : title.Trim(),
            nameof(title),
            500);
    }

    private static string NormalizeForPrompt(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = new(
            value.Select(character => char.IsControl(character) ? ' ' : character)
                .ToArray());
        normalized = normalized.Trim();
        return Take(normalized, maximumLength);
    }

    private static string Take(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string Sha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidatePeriodSchedule(
        FeedDigestPeriod period,
        LocalScheduleDefinition schedule)
    {
        ValidatePeriod(period);
        LocalScheduleFrequency expected = period switch
        {
            FeedDigestPeriod.Daily => LocalScheduleFrequency.Daily,
            FeedDigestPeriod.Weekly => LocalScheduleFrequency.Weekly,
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };
        if (schedule.Frequency != expected)
        {
            throw new ArgumentException(
                "摘要周期与本地计划频率不一致。",
                nameof(schedule));
        }

        // 复用日历计算器的完整字段和时区校验，避免规划器形成第二套规则。
        _ = LocalScheduleCalculator.GetNextOccurrenceUtc(
            schedule,
            DateTimeOffset.UnixEpoch);
    }

    private static void ValidatePeriod(FeedDigestPeriod period)
    {
        if (!Enum.IsDefined(period))
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }
    }

    private static void ValidateScheduleId(
        string scheduleId,
        FeedDigestPeriod period)
    {
        string expected = FeedDigestScheduleIds.For(period);
        if (!string.Equals(scheduleId, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "摘要处理器只能使用已发布的稳定计划 ID。",
                nameof(scheduleId));
        }
    }

    private static void ValidateWindow(FeedDigestWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.StartUtc.Offset != TimeSpan.Zero
            || window.EndUtc.Offset != TimeSpan.Zero
            || window.StartUtc >= window.EndUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }
    }

    private static void ValidateOptions(FeedDigestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateText(options.Model, nameof(options.Model), 128);
        ValidateText(options.PromptVersion, nameof(options.PromptVersion), 128);
        if (options.MaximumEntries is < 1 or > 200
            || options.MaximumCandidateEntries < options.MaximumEntries
            || options.MaximumCandidateEntries > 200
            || options.MaximumCharactersPerEntry is < 1 or > 16_000
            || options.MaximumSourceCharacters is < 128 or > 200_000
            || options.MaximumResponseBytes is < 1 or > 10_000_000
            || options.MaximumOutputTokens is < 1 or > 8_000
            || options.MaximumReportCharacters is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private sealed record PlannedEntry(
        string Id,
        string FeedId,
        string? NormalizedUrl,
        string Title,
        string Content,
        DateTimeOffset TimestampUtc,
        string DeduplicationKey);

    private sealed record SourceBuild(
        string Content,
        int EntryCount);
}
