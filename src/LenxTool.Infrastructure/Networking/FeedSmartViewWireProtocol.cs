using System.Text.Json;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal static class FeedSmartViewWireProtocol
{
    internal const int MaximumResponseBytes = 512 * 1024;
    internal const long MaximumSafeInteger =
        9_007_199_254_740_991;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal static object ToPayload(FeedSmartViewInput input)
    {
        FeedSmartViewInput normalized =
            FeedSmartViewValidator.ValidateAndNormalize(input);
        return new
        {
            normalized.Name,
            normalized.SortOrder,
            normalized.IsEnabled,
            Filter = new
            {
                normalized.Filter.FeedId,
                normalized.Filter.CategoryId,
                ViewKind = normalized.Filter.ViewKind is { } viewKind
                    ? ToWireValue(viewKind)
                    : null,
                ReadFilter = ToWireValue(
                    normalized.Filter.ReadFilter),
                normalized.Filter.FavoritesOnly,
                normalized.Filter.SearchText,
                normalized.Filter.PublishedWithinDays
            }
        };
    }

    internal static FeedSmartViewSnapshot MapSnapshot(
        SnapshotDto dto,
        FeedSmartViewScope expectedScope,
        DateTimeOffset? lastSyncedAt,
        long? minimumExclusiveVersion = null)
    {
        if (!string.Equals(
                dto.Scope,
                ToWireValue(expectedScope),
                StringComparison.Ordinal)
            || dto.ViewSetVersion is < 0 or > MaximumSafeInteger
            || (minimumExclusiveVersion is { } minimum
                && dto.ViewSetVersion <= minimum)
            || dto.GeneratedAt is null
            || dto.GeneratedAt.Value.Offset != TimeSpan.Zero
            || dto.Views is null
            || dto.Views.Count > FeedSmartViewValidator.MaximumViews)
        {
            throw InvalidResponse();
        }
        FeedSmartView[] views = dto.Views
            .Select(MapView)
            .ToArray();
        if (expectedScope == FeedSmartViewScope.Active &&
            views.Any(view => !view.IsEnabled))
        {
            throw InvalidResponse();
        }
        if (views.Select(view => view.Id)
            .Distinct(StringComparer.Ordinal).Count() != views.Length)
        {
            throw InvalidResponse();
        }
        return new(
            dto.ViewSetVersion,
            expectedScope,
            dto.GeneratedAt,
            lastSyncedAt,
            views);
    }

    internal static FeedSmartView MapView(ViewDto? dto)
    {
        if (dto?.Filter is null)
        {
            throw InvalidResponse();
        }
        EntryViewKind? viewKind = dto.Filter.ViewKind switch
        {
            null => null,
            "ARTICLE" => EntryViewKind.Article,
            "PICTURE" => EntryViewKind.Picture,
            "AUDIO" => EntryViewKind.Audio,
            "VIDEO" => EntryViewKind.Video,
            "NOTIFICATION" => EntryViewKind.Notification,
            _ => throw InvalidResponse()
        };
        return FeedSmartViewValidator.ValidateAndNormalize(
            new FeedSmartView(
            dto.Id ?? throw InvalidResponse(),
            dto.Version,
            dto.Name ?? throw InvalidResponse(),
            dto.SortOrder,
            dto.IsEnabled,
            new(
                dto.Filter.FeedId,
                dto.Filter.CategoryId,
                viewKind,
                dto.Filter.ReadFilter switch
                {
                    "ALL" => FeedEntryReadFilter.All,
                    "UNREAD" => FeedEntryReadFilter.Unread,
                    "READ" => FeedEntryReadFilter.Read,
                    _ => throw InvalidResponse()
                },
                dto.Filter.FavoritesOnly,
                dto.Filter.SearchText,
                dto.Filter.PublishedWithinDays)));
    }

    internal static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw InvalidResponse();
        }
        await using Stream input =
            await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int read = await input.ReadAsync(
                buffer,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > MaximumResponseBytes)
            {
                throw InvalidResponse();
            }
            output.Write(buffer, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(
                    output.GetBuffer().AsSpan(0, total),
                    JsonOptions)
                ?? throw InvalidResponse();
        }
        catch (JsonException exception)
        {
            throw new AppException(
                InvalidResponse().Error,
                exception);
        }
    }

    internal static string ValidateId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id))
        {
            throw new ArgumentException(
                "Smart view ID must be a canonical UUID.",
                nameof(value));
        }
        return id.ToString("D");
    }

    internal static void ValidateVersion(long version)
    {
        if (version is < 0 or > MaximumSafeInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }
    }

    internal static AppException InvalidResponse() => new(new(
        AppErrorCode.ProviderUnavailable,
        "智能视图响应无效",
        "云服务没有返回可安全应用的智能视图快照。",
        "已保留上次成功视图；请刷新后再决定是否重试。",
        Provider: "LenxTool Worker",
        IsRetryable: true));

    private static string ToWireValue(
        FeedSmartViewScope scope) =>
        scope switch
        {
            FeedSmartViewScope.Active => "ACTIVE",
            FeedSmartViewScope.All => "ALL",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };

    private static string ToWireValue(EntryViewKind kind) =>
        kind.ToString().ToUpperInvariant();

    private static string ToWireValue(
        FeedEntryReadFilter filter) =>
        filter.ToString().ToUpperInvariant();

    internal sealed class SnapshotDto
    {
        public long ViewSetVersion { get; init; }
        public string? Scope { get; init; }
        public DateTimeOffset? GeneratedAt { get; init; }
        public List<ViewDto?>? Views { get; init; }
    }

    internal sealed class MutationDto
    {
        public long ViewSetVersion { get; init; }
        public ViewDto? View { get; init; }
        public string? DeletedViewId { get; init; }
    }

    internal sealed class ViewDto
    {
        public string? Id { get; init; }
        public int Version { get; init; }
        public string? Name { get; init; }
        public int SortOrder { get; init; }
        public bool IsEnabled { get; init; }
        public FilterDto? Filter { get; init; }
    }

    internal sealed class FilterDto
    {
        public string? FeedId { get; init; }
        public string? CategoryId { get; init; }
        public string? ViewKind { get; init; }
        public string? ReadFilter { get; init; }
        public bool FavoritesOnly { get; init; }
        public string? SearchText { get; init; }
        public int? PublishedWithinDays { get; init; }
    }
}
