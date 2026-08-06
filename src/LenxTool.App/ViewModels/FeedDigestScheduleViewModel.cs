using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record FeedDigestScopeChoice(
    string? FeedId,
    string? CategoryId,
    string Label)
{
    public string? Id => FeedId ?? CategoryId;

    public FeedDigestScope CreateScope(string searchText) =>
        FeedDigestScope.Normalize(new(FeedId, CategoryId, searchText));
}

public sealed record FeedDigestWeekdayChoice(
    DayOfWeek Value,
    string Label);

/// <summary>
/// 本地摘要计划只编辑本机计划和范围；可选项来自只读 ACTIVE 目录，
/// 不会把用户的关键词、报告正文或计划时间写回共享目录。
/// </summary>
public sealed class FeedDigestScheduleViewModel : ObservableObject, IDisposable
{
    private readonly IFeedDigestScheduleService _schedules;
    private readonly IFeedCatalogRepository _catalog;
    private bool _isBusy;
    private string _status = "正在读取本地摘要计划…";
    private bool _dailyEnabled;
    private string _dailyTimeText = "08:00";
    private FeedDigestScopeChoice _dailySelectedScope;
    private string _dailySearchText = string.Empty;
    private string _dailyNextRunText = "计划未启用";
    private bool _weeklyEnabled;
    private string _weeklyTimeText = "08:00";
    private FeedDigestScopeChoice _weeklySelectedScope;
    private FeedDigestWeekdayChoice _selectedWeeklyDay;
    private string _weeklySearchText = string.Empty;
    private string _weeklyNextRunText = "计划未启用";
    private string _dailyTimeZoneId = TimeZoneInfo.Local.Id;
    private string _weeklyTimeZoneId = TimeZoneInfo.Local.Id;

    public FeedDigestScheduleViewModel(
        IFeedDigestScheduleService schedules,
        IFeedCatalogRepository catalog)
    {
        _schedules = schedules;
        _catalog = catalog;
        ScopeChoices = [new(null, null, "所有启用订阅")];
        WeekdayChoices =
        [
            new(DayOfWeek.Monday, "周一"),
            new(DayOfWeek.Tuesday, "周二"),
            new(DayOfWeek.Wednesday, "周三"),
            new(DayOfWeek.Thursday, "周四"),
            new(DayOfWeek.Friday, "周五"),
            new(DayOfWeek.Saturday, "周六"),
            new(DayOfWeek.Sunday, "周日")
        ];
        _dailySelectedScope = ScopeChoices[0];
        _weeklySelectedScope = ScopeChoices[0];
        _selectedWeeklyDay = WeekdayChoices[0];
        RefreshCommand = new(LoadAsync, () => !IsBusy);
        SaveDailyCommand = new(
            cancellationToken => SaveAsync(FeedDigestPeriod.Daily, cancellationToken),
            () => !IsBusy);
        SaveWeeklyCommand = new(
            cancellationToken => SaveAsync(FeedDigestPeriod.Weekly, cancellationToken),
            () => !IsBusy);
    }

    public ObservableCollection<FeedDigestScopeChoice> ScopeChoices { get; }
    public IReadOnlyList<FeedDigestWeekdayChoice> WeekdayChoices { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveDailyCommand { get; }
    public AsyncRelayCommand SaveWeeklyCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                SaveDailyCommand.NotifyCanExecuteChanged();
                SaveWeeklyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool DailyEnabled
    {
        get => _dailyEnabled;
        set => SetProperty(ref _dailyEnabled, value);
    }

    public string DailyTimeText
    {
        get => _dailyTimeText;
        set => SetProperty(ref _dailyTimeText, value ?? string.Empty);
    }

    public FeedDigestScopeChoice DailySelectedScope
    {
        get => _dailySelectedScope;
        set => SetProperty(ref _dailySelectedScope, value ?? ScopeChoices[0]);
    }

    public string DailySearchText
    {
        get => _dailySearchText;
        set => SetProperty(ref _dailySearchText, value ?? string.Empty);
    }

    public string DailyNextRunText
    {
        get => _dailyNextRunText;
        private set => SetProperty(ref _dailyNextRunText, value);
    }

    public bool WeeklyEnabled
    {
        get => _weeklyEnabled;
        set => SetProperty(ref _weeklyEnabled, value);
    }

    public string WeeklyTimeText
    {
        get => _weeklyTimeText;
        set => SetProperty(ref _weeklyTimeText, value ?? string.Empty);
    }

    public FeedDigestScopeChoice WeeklySelectedScope
    {
        get => _weeklySelectedScope;
        set => SetProperty(ref _weeklySelectedScope, value ?? ScopeChoices[0]);
    }

    public FeedDigestWeekdayChoice SelectedWeeklyDay
    {
        get => _selectedWeeklyDay;
        set => SetProperty(ref _selectedWeeklyDay, value ?? WeekdayChoices[0]);
    }

    public string WeeklySearchText
    {
        get => _weeklySearchText;
        set => SetProperty(ref _weeklySearchText, value ?? string.Empty);
    }

    public string WeeklyNextRunText
    {
        get => _weeklyNextRunText;
        private set => SetProperty(ref _weeklyNextRunText, value);
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        LoadAsync(cancellationToken);

    public void Dispose()
    {
        RefreshCommand.Dispose();
        SaveDailyCommand.Dispose();
        SaveWeeklyCommand.Dispose();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            FeedCatalogSnapshot catalog = await _catalog.GetCatalogAsync(
                    FeedCatalogScope.Active,
                    cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidDataException("本机尚无 ACTIVE 订阅目录，请先同步目录。");
            FeedDigestScheduleState daily = await _schedules.GetAsync(
                FeedDigestPeriod.Daily,
                cancellationToken).ConfigureAwait(true);
            FeedDigestScheduleState weekly = await _schedules.GetAsync(
                FeedDigestPeriod.Weekly,
                cancellationToken).ConfigureAwait(true);
            RebuildScopeChoices(catalog);
            ApplyState(daily);
            ApplyState(weekly);
            Status = "本地摘要计划已加载；范围仅使用 ACTIVE 订阅目录。";
        }
        catch (Exception exception) when (
            exception is AppException
                or ArgumentException
                or InvalidDataException
                or TimeZoneNotFoundException
                or InvalidTimeZoneException)
        {
            Status = FormatFailure(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync(
        FeedDigestPeriod period,
        CancellationToken cancellationToken)
    {
        FeedDigestScheduleConfiguration configuration;
        try
        {
            configuration = BuildConfiguration(period);
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
            return;
        }

        IsBusy = true;
        try
        {
            FeedDigestScheduleState saved = await _schedules.SaveAsync(
                configuration,
                cancellationToken).ConfigureAwait(true);
            ApplyState(saved);
            Status = period == FeedDigestPeriod.Daily
                ? "每日摘要计划已保存。"
                : "每周摘要计划已保存。";
        }
        catch (Exception exception) when (
            exception is AppException
                or ArgumentException
                or InvalidDataException
                or TimeZoneNotFoundException
                or InvalidTimeZoneException)
        {
            Status = FormatFailure(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private FeedDigestScheduleConfiguration BuildConfiguration(
        FeedDigestPeriod period)
    {
        string timeText = period == FeedDigestPeriod.Daily
            ? DailyTimeText
            : WeeklyTimeText;
        if (!TimeOnly.TryParseExact(
                timeText.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out TimeOnly localTime))
        {
            throw new ArgumentException("执行时间必须使用 HH:mm 的 24 小时格式。");
        }

        FeedDigestScopeChoice scope = period == FeedDigestPeriod.Daily
            ? DailySelectedScope
            : WeeklySelectedScope;
        string searchText = period == FeedDigestPeriod.Daily
            ? DailySearchText
            : WeeklySearchText;
        return new(
            period,
            localTime,
            period == FeedDigestPeriod.Weekly
                ? SelectedWeeklyDay.Value
                : null,
            period == FeedDigestPeriod.Daily
                ? _dailyTimeZoneId
                : _weeklyTimeZoneId,
            period == FeedDigestPeriod.Daily
                ? DailyEnabled
                : WeeklyEnabled,
            scope.CreateScope(searchText));
    }

    private void RebuildScopeChoices(FeedCatalogSnapshot catalog)
    {
        ScopeChoices.Clear();
        ScopeChoices.Add(new(null, null, "所有启用订阅"));
        foreach (FeedCategory category in catalog.Categories
                     .Where(category => category.IsEnabled)
                     .OrderBy(category => category.SortOrder)
                     .ThenBy(category => category.Name, StringComparer.CurrentCulture))
        {
            ScopeChoices.Add(new(
                null,
                category.Id,
                $"分类 · {category.Name}"));
        }
        Dictionary<string, string> categoryNames = catalog.Categories
            .Where(category => category.IsEnabled)
            .ToDictionary(
                category => category.Id,
                category => category.Name,
                StringComparer.Ordinal);
        foreach (FeedCatalogItem feed in catalog.Feeds
                     .Where(feed => feed.IsEnabled)
                     .OrderBy(feed => feed.SortOrder)
                     .ThenBy(feed => feed.DisplayName, StringComparer.CurrentCulture))
        {
            string category = feed.CategoryId is not null
                && categoryNames.TryGetValue(feed.CategoryId, out string? name)
                    ? $" / {name}"
                    : string.Empty;
            ScopeChoices.Add(new(
                feed.Id,
                feed.CategoryId,
                $"Feed · {feed.DisplayName}{category}"));
        }
    }

    private void ApplyState(FeedDigestScheduleState state)
    {
        FeedDigestScopeChoice selected = FindScope(state.Scope);
        string nextRun = FormatNextRun(state);
        if (state.Period == FeedDigestPeriod.Daily)
        {
            _dailyTimeZoneId = state.TimeZoneId;
            DailyEnabled = state.IsEnabled;
            DailyTimeText = state.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            DailySelectedScope = selected;
            DailySearchText = state.Scope.SearchText ?? string.Empty;
            DailyNextRunText = nextRun;
            return;
        }
        if (state.Period != FeedDigestPeriod.Weekly)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        _weeklyTimeZoneId = state.TimeZoneId;
        WeeklyEnabled = state.IsEnabled;
        WeeklyTimeText = state.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        WeeklySelectedScope = selected;
        SelectedWeeklyDay = WeekdayChoices.Single(
            choice => choice.Value == (state.WeeklyDay ?? DayOfWeek.Monday));
        WeeklySearchText = state.Scope.SearchText ?? string.Empty;
        WeeklyNextRunText = nextRun;
    }

    private FeedDigestScopeChoice FindScope(FeedDigestScope scope)
    {
        FeedDigestScopeChoice? match = ScopeChoices.FirstOrDefault(choice =>
            string.Equals(choice.FeedId, scope.FeedId, StringComparison.Ordinal)
            && string.Equals(choice.CategoryId, scope.CategoryId, StringComparison.Ordinal));
        if (match is not null)
        {
            return match;
        }
        var stale = new FeedDigestScopeChoice(
            scope.FeedId,
            scope.CategoryId,
            $"已失效范围 · {scope.FeedId ?? scope.CategoryId}");
        ScopeChoices.Add(stale);
        return stale;
    }

    private static string FormatNextRun(FeedDigestScheduleState state)
    {
        if (!state.IsEnabled || state.NextRunAtUtc is null)
        {
            return "计划未启用";
        }
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(state.TimeZoneId);
        DateTimeOffset local = TimeZoneInfo.ConvertTime(state.NextRunAtUtc.Value, zone);
        return $"下一次 · {local:yyyy-MM-dd HH:mm} · {state.TimeZoneId}";
    }

    private static string FormatFailure(Exception exception) =>
        exception is AppException appException
            ? $"{appException.Error.UserMessage} {appException.Error.Suggestion}".Trim()
            : exception.Message;
}
