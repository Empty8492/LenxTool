using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Feeds;

public sealed class FeedAiPolicyResolverTests
{
    private const string CategoryId = "10000000-0000-4000-8000-000000000013";
    private const string FeedId = "20000000-0000-4000-8000-000000000013";

    [Fact]
    public void ResolveUsesSafeDefaultsWhenCatalogHasNoPolicyFields()
    {
        FeedCatalogSnapshot catalog = Catalog(categoryPolicy: null, feedPolicy: null, defaults: null);

        ResolvedFeedAiPolicy policy = FeedAiPolicyResolver.Resolve(catalog, catalog.Feeds[0]);

        Assert.True(policy.ManualSummaryEnabled);
        Assert.False(policy.AutoSummaryEnabled);
        Assert.False(policy.AutoTranslationEnabled);
        Assert.Equal("zh-Hans", policy.TranslationTargetLanguage);
        Assert.Equal(20, policy.DailyEntryLimit);
        Assert.Equal(1, policy.MaxConcurrency);
    }

    [Fact]
    public void ResolveAppliesFeedThenCategoryThenGlobalPrecedence()
    {
        var defaults = new FeedAiPolicy(
            FeedAiPolicySwitch.Enabled,
            FeedAiPolicySwitch.Disabled,
            FeedAiPolicySwitch.Disabled,
            "zh-Hans",
            20,
            1);
        var category = new FeedAiPolicy(
            FeedAiPolicySwitch.Disabled,
            FeedAiPolicySwitch.Enabled,
            FeedAiPolicySwitch.Inherit,
            "ja",
            12,
            2);
        var feed = new FeedAiPolicy(
            FeedAiPolicySwitch.Inherit,
            FeedAiPolicySwitch.Disabled,
            FeedAiPolicySwitch.Enabled,
            null,
            8,
            null);

        ResolvedFeedAiPolicy policy = FeedAiPolicyResolver.Resolve(
            Catalog(category, feed, defaults),
            FeedId);

        Assert.False(policy.ManualSummaryEnabled);
        Assert.False(policy.AutoSummaryEnabled);
        Assert.True(policy.AutoTranslationEnabled);
        Assert.Equal("ja", policy.TranslationTargetLanguage);
        Assert.Equal(8, policy.DailyEntryLimit);
        Assert.Equal(2, policy.MaxConcurrency);
    }

    [Fact]
    public void ResolveRejectsInvalidDefaultsAndUnknownFeed()
    {
        FeedCatalogSnapshot invalid = Catalog(
            categoryPolicy: null,
            feedPolicy: null,
            defaults: new(
                FeedAiPolicySwitch.Inherit,
                FeedAiPolicySwitch.Disabled,
                FeedAiPolicySwitch.Disabled,
                null,
                null,
                null));

        Assert.Throws<InvalidDataException>(() => FeedAiPolicyResolver.Resolve(invalid, FeedId));
        Assert.Throws<KeyNotFoundException>(() =>
            FeedAiPolicyResolver.Resolve(Catalog(null, null, null), "missing-feed"));
    }

    private static FeedCatalogSnapshot Catalog(
        FeedAiPolicy? categoryPolicy,
        FeedAiPolicy? feedPolicy,
        FeedAiPolicy? defaults)
    {
        var category = new FeedCategory(
            CategoryId,
            "AI",
            "ai",
            0,
            true,
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            categoryPolicy);
        var feed = new FeedCatalogItem(
            FeedId,
            "https://example.com/feed",
            "https://example.com/feed",
            "AI Feed",
            "https://example.com/",
            CategoryId,
            FeedViewKind.Article,
            60,
            0,
            true,
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            FeedFullTextPolicy.None,
            feedPolicy);
        return new(
            new(1, FeedCatalogScope.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            [category],
            [feed],
            defaults);
    }
}
