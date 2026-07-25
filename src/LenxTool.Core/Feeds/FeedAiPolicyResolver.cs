using LenxTool.Core.Models;

namespace LenxTool.Core.Feeds;

public static class FeedAiPolicyResolver
{
    private static readonly HashSet<string> TargetLanguages =
        new(StringComparer.Ordinal) { "zh-Hans", "en", "ja", "ko" };

    public static ResolvedFeedAiPolicy Resolve(FeedCatalogSnapshot catalog, string feedId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        FeedCatalogItem feed = catalog.Feeds.FirstOrDefault(
            candidate => string.Equals(candidate.Id, feedId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Feed '{feedId}' is not present in the catalog.");
        return Resolve(catalog, feed);
    }

    public static ResolvedFeedAiPolicy Resolve(FeedCatalogSnapshot catalog, FeedCatalogItem feed)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(feed);
        FeedAiPolicy defaults = catalog.AiPolicyDefaults ?? FeedAiPolicy.SafeDefaults;
        ValidateDefaults(defaults);

        FeedAiPolicy categoryPolicy = FeedAiPolicy.Inherited;
        if (feed.CategoryId is not null)
        {
            FeedCategory category = catalog.Categories.FirstOrDefault(
                candidate => string.Equals(candidate.Id, feed.CategoryId, StringComparison.Ordinal))
                ?? throw new InvalidDataException("Feed AI policy references a missing category.");
            categoryPolicy = category.AiPolicy ?? FeedAiPolicy.Inherited;
        }

        FeedAiPolicy feedPolicy = feed.AiPolicy ?? FeedAiPolicy.Inherited;
        ValidateOverride(categoryPolicy);
        ValidateOverride(feedPolicy);
        return new(
            ResolveSwitch(feedPolicy.ManualSummary, categoryPolicy.ManualSummary, defaults.ManualSummary),
            ResolveSwitch(feedPolicy.AutoSummary, categoryPolicy.AutoSummary, defaults.AutoSummary),
            ResolveSwitch(feedPolicy.AutoTranslation, categoryPolicy.AutoTranslation, defaults.AutoTranslation),
            feedPolicy.TranslationTargetLanguage
                ?? categoryPolicy.TranslationTargetLanguage
                ?? defaults.TranslationTargetLanguage!,
            feedPolicy.DailyEntryLimit
                ?? categoryPolicy.DailyEntryLimit
                ?? defaults.DailyEntryLimit!.Value,
            feedPolicy.MaxConcurrency
                ?? categoryPolicy.MaxConcurrency
                ?? defaults.MaxConcurrency!.Value);
    }

    private static bool ResolveSwitch(
        FeedAiPolicySwitch feed,
        FeedAiPolicySwitch category,
        FeedAiPolicySwitch defaults)
    {
        FeedAiPolicySwitch effective = feed != FeedAiPolicySwitch.Inherit
            ? feed
            : category != FeedAiPolicySwitch.Inherit
                ? category
                : defaults;
        return effective switch
        {
            FeedAiPolicySwitch.Enabled => true,
            FeedAiPolicySwitch.Disabled => false,
            _ => throw new InvalidDataException("The effective Feed AI policy cannot inherit.")
        };
    }

    private static void ValidateDefaults(FeedAiPolicy policy)
    {
        if (policy.ManualSummary == FeedAiPolicySwitch.Inherit
            || policy.AutoSummary == FeedAiPolicySwitch.Inherit
            || policy.AutoTranslation == FeedAiPolicySwitch.Inherit
            || policy.TranslationTargetLanguage is null
            || policy.DailyEntryLimit is null
            || policy.MaxConcurrency is null)
        {
            throw new InvalidDataException("Feed AI policy defaults must be fully resolved.");
        }
        ValidateCommon(policy);
    }

    private static void ValidateOverride(FeedAiPolicy policy) => ValidateCommon(policy);

    private static void ValidateCommon(FeedAiPolicy policy)
    {
        if (!Enum.IsDefined(policy.ManualSummary)
            || !Enum.IsDefined(policy.AutoSummary)
            || !Enum.IsDefined(policy.AutoTranslation)
            || (policy.TranslationTargetLanguage is not null
                && !TargetLanguages.Contains(policy.TranslationTargetLanguage))
            || policy.DailyEntryLimit is < 1 or > 1000
            || policy.MaxConcurrency is < 1 or > 4)
        {
            throw new InvalidDataException("Feed AI policy contains an invalid value.");
        }
    }
}
