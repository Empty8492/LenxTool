using System.Text;
using LenxTool.Core.Models;

namespace LenxTool.Core.Feeds;

public static class FeedSmartViewValidator
{
    public const int MaximumViews = 100;
    public const int MaximumNameLength = 120;
    public const int MaximumSearchLength = 200;
    public const int MaximumSortOrder = 1_000;
    public const int MaximumPublishedWithinDays = 365;

    public static FeedSmartView ValidateAndNormalize(
        FeedSmartView value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Guid.TryParseExact(value.Id, "D", out Guid id))
        {
            throw new ArgumentException(
                "智能视图 ID 必须是规范 GUID。",
                nameof(value));
        }
        if (value.Version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        FeedSmartViewInput input = ValidateAndNormalize(
            new FeedSmartViewInput(
                value.Name,
                value.SortOrder,
                value.IsEnabled,
                value.Filter));
        return new(
            id.ToString("D"),
            value.Version,
            input.Name,
            input.SortOrder,
            input.IsEnabled,
            input.Filter);
    }

    public static FeedSmartViewInput ValidateAndNormalize(
        FeedSmartViewInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.Filter);
        string name = NormalizeText(
            value.Name,
            MaximumNameLength,
            required: true,
            nameof(value.Name))!;
        if (value.SortOrder is < 0 or > MaximumSortOrder)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        FeedSmartViewFilter filter = value.Filter;
        ValidateOptionalId(filter.FeedId, nameof(filter.FeedId));
        ValidateOptionalId(
            filter.CategoryId,
            nameof(filter.CategoryId));
        if (filter.ViewKind is { } viewKind &&
            !Enum.IsDefined(viewKind))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (!Enum.IsDefined(filter.ReadFilter))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (filter.PublishedWithinDays is { } days &&
            (days < 1 || days > MaximumPublishedWithinDays))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        string? searchText = NormalizeText(
            filter.SearchText,
            MaximumSearchLength,
            required: false,
            nameof(filter.SearchText));
        return new(
            name,
            value.SortOrder,
            value.IsEnabled,
            filter with
            {
                FeedId = NormalizeOptionalId(filter.FeedId),
                CategoryId = NormalizeOptionalId(filter.CategoryId),
                SearchText = searchText
            });
    }

    public static FeedEntryQuery Apply(
        FeedSmartView view,
        DateTimeOffset now,
        int offset,
        int limit,
        string localProfile = "default")
    {
        FeedSmartView normalized = ValidateAndNormalize(view);
        if (!normalized.IsEnabled)
        {
            throw new InvalidOperationException(
                "不能应用未启用的共享智能视图。");
        }
        if (now.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "智能视图查询基准时间必须是 UTC。",
                nameof(now));
        }
        if (offset < 0 || limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(localProfile);

        FeedSmartViewFilter filter = normalized.Filter;
        return new(
            filter.SearchText,
            filter.FeedId,
            filter.CategoryId,
            filter.PublishedWithinDays is { } days
                ? now.AddDays(-days)
                : null,
            PublishedBefore: null,
            filter.ReadFilter,
            offset,
            limit,
            ActiveOnly: true,
            filter.FavoritesOnly,
            TagId: null,
            localProfile,
            IncludeHidden: false,
            filter.ViewKind);
    }

    private static void ValidateOptionalId(
        string? value,
        string parameterName)
    {
        if (value is not null &&
            !Guid.TryParseExact(value, "D", out _))
        {
            throw new ArgumentException(
                "智能视图引用必须是规范 GUID。",
                parameterName);
        }
    }

    private static string? NormalizeOptionalId(string? value) =>
        value is null
            ? null
            : Guid.ParseExact(value, "D").ToString("D");

    private static string? NormalizeText(
        string? value,
        int maximumLength,
        bool required,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                throw new ArgumentException(
                    "智能视图文本不能为空。",
                    parameterName);
            }
            return null;
        }

        var result = new StringBuilder(
            Math.Min(value.Length, maximumLength));
        bool needsSpace = false;
        foreach (char character in value.Normalize(
                     NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(character))
            {
                needsSpace = result.Length > 0;
                continue;
            }
            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    "智能视图文本不能包含控制字符。",
                    parameterName);
            }
            if (needsSpace)
            {
                result.Append(' ');
                needsSpace = false;
            }
            result.Append(character);
            if (result.Length > maximumLength)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
        return result.ToString();
    }
}
