namespace LenxTool.Core.Models;

public enum FeedAiPolicySwitch
{
    Inherit,
    Enabled,
    Disabled
}

public sealed record FeedAiPolicy(
    FeedAiPolicySwitch ManualSummary,
    FeedAiPolicySwitch AutoSummary,
    FeedAiPolicySwitch AutoTranslation,
    string? TranslationTargetLanguage,
    int? DailyEntryLimit,
    int? MaxConcurrency)
{
    public static FeedAiPolicy Inherited { get; } = new(
        FeedAiPolicySwitch.Inherit,
        FeedAiPolicySwitch.Inherit,
        FeedAiPolicySwitch.Inherit,
        null,
        null,
        null);

    public static FeedAiPolicy SafeDefaults { get; } = new(
        FeedAiPolicySwitch.Enabled,
        FeedAiPolicySwitch.Disabled,
        FeedAiPolicySwitch.Disabled,
        "zh-Hans",
        20,
        1);
}

public sealed record ResolvedFeedAiPolicy(
    bool ManualSummaryEnabled,
    bool AutoSummaryEnabled,
    bool AutoTranslationEnabled,
    string TranslationTargetLanguage,
    int DailyEntryLimit,
    int MaxConcurrency);
