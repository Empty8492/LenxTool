using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record FeedAiPolicySwitchChoice(FeedAiPolicySwitch Policy, string Label);
public sealed record FeedAiLanguageChoice(string? Language, string Label);

public sealed partial class FeedAdminViewModel
{
    private FeedAiPolicy _aiPolicyDefaults = FeedAiPolicy.SafeDefaults;
    private FeedAiPolicySwitch _categoryManualSummaryPolicy = FeedAiPolicySwitch.Inherit;
    private FeedAiPolicySwitch _categoryAutoSummaryPolicy = FeedAiPolicySwitch.Inherit;
    private FeedAiPolicySwitch _categoryAutoTranslationPolicy = FeedAiPolicySwitch.Inherit;
    private string? _categoryTranslationTargetLanguage;
    private int? _categoryAiDailyEntryLimit;
    private int? _categoryAiMaxConcurrency;
    private FeedAiPolicySwitch _feedManualSummaryPolicy = FeedAiPolicySwitch.Inherit;
    private FeedAiPolicySwitch _feedAutoSummaryPolicy = FeedAiPolicySwitch.Inherit;
    private FeedAiPolicySwitch _feedAutoTranslationPolicy = FeedAiPolicySwitch.Inherit;
    private string? _feedTranslationTargetLanguage;
    private int? _feedAiDailyEntryLimit;
    private int? _feedAiMaxConcurrency;

    public IReadOnlyList<FeedAiPolicySwitchChoice> AiPolicySwitchChoices { get; } =
    [
        new(FeedAiPolicySwitch.Inherit, "继承上级"),
        new(FeedAiPolicySwitch.Enabled, "允许"),
        new(FeedAiPolicySwitch.Disabled, "关闭")
    ];

    public IReadOnlyList<FeedAiLanguageChoice> AiLanguageChoices { get; } =
    [
        new(null, "继承上级"),
        new("zh-Hans", "简体中文"),
        new("en", "English"),
        new("ja", "日本語"),
        new("ko", "한국어")
    ];

    public FeedAiPolicySwitch CategoryManualSummaryPolicy
    {
        get => _categoryManualSummaryPolicy;
        set
        {
            if (SetProperty(ref _categoryManualSummaryPolicy, value))
                NotifyCategoryAiPolicyChanged();
        }
    }

    public FeedAiPolicySwitch CategoryAutoSummaryPolicy
    {
        get => _categoryAutoSummaryPolicy;
        set
        {
            if (SetProperty(ref _categoryAutoSummaryPolicy, value))
                NotifyCategoryAiPolicyChanged();
        }
    }

    public FeedAiPolicySwitch CategoryAutoTranslationPolicy
    {
        get => _categoryAutoTranslationPolicy;
        set
        {
            if (SetProperty(ref _categoryAutoTranslationPolicy, value))
                NotifyCategoryAiPolicyChanged();
        }
    }

    public string? CategoryTranslationTargetLanguage
    {
        get => _categoryTranslationTargetLanguage;
        set
        {
            if (SetProperty(ref _categoryTranslationTargetLanguage, value))
                NotifyCategoryAiPolicyChanged();
        }
    }

    public int? CategoryAiDailyEntryLimit
    {
        get => _categoryAiDailyEntryLimit;
        set
        {
            if (SetProperty(ref _categoryAiDailyEntryLimit, value))
                NotifyCategoryAiPolicyChanged();
        }
    }

    public int? CategoryAiMaxConcurrency
    {
        get => _categoryAiMaxConcurrency;
        set
        {
            if (SetProperty(ref _categoryAiMaxConcurrency, value))
                NotifyCategoryAiPolicyChanged();
        }
    }

    public FeedAiPolicySwitch FeedManualSummaryPolicy
    {
        get => _feedManualSummaryPolicy;
        set
        {
            if (SetProperty(ref _feedManualSummaryPolicy, value))
                NotifyFeedAiPolicyChanged();
        }
    }

    public FeedAiPolicySwitch FeedAutoSummaryPolicy
    {
        get => _feedAutoSummaryPolicy;
        set
        {
            if (SetProperty(ref _feedAutoSummaryPolicy, value))
                NotifyFeedAiPolicyChanged();
        }
    }

    public FeedAiPolicySwitch FeedAutoTranslationPolicy
    {
        get => _feedAutoTranslationPolicy;
        set
        {
            if (SetProperty(ref _feedAutoTranslationPolicy, value))
                NotifyFeedAiPolicyChanged();
        }
    }

    public string? FeedTranslationTargetLanguage
    {
        get => _feedTranslationTargetLanguage;
        set
        {
            if (SetProperty(ref _feedTranslationTargetLanguage, value))
                NotifyFeedAiPolicyChanged();
        }
    }

    public int? FeedAiDailyEntryLimit
    {
        get => _feedAiDailyEntryLimit;
        set
        {
            if (SetProperty(ref _feedAiDailyEntryLimit, value))
                NotifyFeedAiPolicyChanged();
        }
    }

    public int? FeedAiMaxConcurrency
    {
        get => _feedAiMaxConcurrency;
        set
        {
            if (SetProperty(ref _feedAiMaxConcurrency, value))
                NotifyFeedAiPolicyChanged();
        }
    }

    public string CategoryAiUsageEstimate => FormatUsageEstimate(
        ResolveOverride(CreateCategoryAiPolicy(), ResolveDefaults()));

    public string FeedAiUsageEstimate
    {
        get
        {
            ResolvedFeedAiPolicy parent = ResolveDefaults();
            FeedCategory? category = Categories.FirstOrDefault(item =>
                string.Equals(item.Id, SelectedCategoryId, StringComparison.Ordinal));
            if (category is not null)
                parent = ResolveOverride(category.AiPolicy ?? FeedAiPolicy.Inherited, parent);
            return FormatUsageEstimate(ResolveOverride(CreateFeedAiPolicy(), parent));
        }
    }

    private void ApplyCategoryAiPolicy(FeedAiPolicy? policy) =>
        ApplyCategoryAiPolicyValues(policy ?? FeedAiPolicy.Inherited);

    private void ResetCategoryAiPolicy() =>
        ApplyCategoryAiPolicyValues(FeedAiPolicy.Inherited);

    private void ApplyCategoryAiPolicyValues(FeedAiPolicy policy)
    {
        CategoryManualSummaryPolicy = policy.ManualSummary;
        CategoryAutoSummaryPolicy = policy.AutoSummary;
        CategoryAutoTranslationPolicy = policy.AutoTranslation;
        CategoryTranslationTargetLanguage = policy.TranslationTargetLanguage;
        CategoryAiDailyEntryLimit = policy.DailyEntryLimit;
        CategoryAiMaxConcurrency = policy.MaxConcurrency;
    }

    private FeedAiPolicy CreateCategoryAiPolicy() => new(
        CategoryManualSummaryPolicy,
        CategoryAutoSummaryPolicy,
        CategoryAutoTranslationPolicy,
        CategoryTranslationTargetLanguage,
        CategoryAiDailyEntryLimit,
        CategoryAiMaxConcurrency);

    private void ApplyFeedAiPolicy(FeedAiPolicy? policy) =>
        ApplyFeedAiPolicyValues(policy ?? FeedAiPolicy.Inherited);

    private void ResetFeedAiPolicy() =>
        ApplyFeedAiPolicyValues(FeedAiPolicy.Inherited);

    private void ApplyFeedAiPolicyValues(FeedAiPolicy policy)
    {
        FeedManualSummaryPolicy = policy.ManualSummary;
        FeedAutoSummaryPolicy = policy.AutoSummary;
        FeedAutoTranslationPolicy = policy.AutoTranslation;
        FeedTranslationTargetLanguage = policy.TranslationTargetLanguage;
        FeedAiDailyEntryLimit = policy.DailyEntryLimit;
        FeedAiMaxConcurrency = policy.MaxConcurrency;
    }

    private FeedAiPolicy CreateFeedAiPolicy() => new(
        FeedManualSummaryPolicy,
        FeedAutoSummaryPolicy,
        FeedAutoTranslationPolicy,
        FeedTranslationTargetLanguage,
        FeedAiDailyEntryLimit,
        FeedAiMaxConcurrency);

    private void SetAiPolicyDefaults(FeedAiPolicy? defaults)
    {
        _aiPolicyDefaults = defaults ?? FeedAiPolicy.SafeDefaults;
        OnPropertyChanged(nameof(CategoryAiUsageEstimate));
        OnPropertyChanged(nameof(FeedAiUsageEstimate));
    }

    private void NotifyCategoryAiPolicyChanged()
    {
        NotifyCategoryCommands();
        OnPropertyChanged(nameof(CategoryAiUsageEstimate));
    }

    private void NotifyFeedAiPolicyChanged()
    {
        NotifyFeedCommands();
        OnPropertyChanged(nameof(FeedAiUsageEstimate));
    }

    private ResolvedFeedAiPolicy ResolveDefaults() => new(
        _aiPolicyDefaults.ManualSummary == FeedAiPolicySwitch.Enabled,
        _aiPolicyDefaults.AutoSummary == FeedAiPolicySwitch.Enabled,
        _aiPolicyDefaults.AutoTranslation == FeedAiPolicySwitch.Enabled,
        _aiPolicyDefaults.TranslationTargetLanguage ?? "zh-Hans",
        _aiPolicyDefaults.DailyEntryLimit ?? 20,
        _aiPolicyDefaults.MaxConcurrency ?? 1);

    private static ResolvedFeedAiPolicy ResolveOverride(
        FeedAiPolicy policy,
        ResolvedFeedAiPolicy parent) => new(
            ResolveSwitch(policy.ManualSummary, parent.ManualSummaryEnabled),
            ResolveSwitch(policy.AutoSummary, parent.AutoSummaryEnabled),
            ResolveSwitch(policy.AutoTranslation, parent.AutoTranslationEnabled),
            policy.TranslationTargetLanguage ?? parent.TranslationTargetLanguage,
            policy.DailyEntryLimit ?? parent.DailyEntryLimit,
            policy.MaxConcurrency ?? parent.MaxConcurrency);

    private static bool ResolveSwitch(FeedAiPolicySwitch policy, bool inherited) => policy switch
    {
        FeedAiPolicySwitch.Inherit => inherited,
        FeedAiPolicySwitch.Enabled => true,
        FeedAiPolicySwitch.Disabled => false,
        _ => inherited
    };

    private static string FormatUsageEstimate(ResolvedFeedAiPolicy policy)
    {
        int automaticTasks = (policy.AutoSummaryEnabled ? 1 : 0)
            + (policy.AutoTranslationEnabled ? 1 : 0);
        return automaticTasks == 0
            ? $"自动处理关闭 · 预计 0 个条目/日 · 并发 {policy.MaxConcurrency}"
            : $"最多 {policy.DailyEntryLimit} 个条目/日 · {automaticTasks} 项自动任务 · 并发 {policy.MaxConcurrency}";
    }

    private static bool IsValidAiPolicy(FeedAiPolicy policy) =>
        Enum.IsDefined(policy.ManualSummary)
        && Enum.IsDefined(policy.AutoSummary)
        && Enum.IsDefined(policy.AutoTranslation)
        && (policy.TranslationTargetLanguage is null
            || policy.TranslationTargetLanguage is "zh-Hans" or "en" or "ja" or "ko")
        && policy.DailyEntryLimit is null or >= 1 and <= 1000
        && policy.MaxConcurrency is null or >= 1 and <= 4;
}
