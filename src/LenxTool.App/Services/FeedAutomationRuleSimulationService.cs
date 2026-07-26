using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public sealed class FeedAutomationRuleSimulationService(
    IFeedEntryRepository entryRepository,
    IFeedCatalogRepository catalogRepository)
    : IFeedAutomationRuleSimulationService
{
    public const int MaximumEntries = 50;
    private const string SimulationRuleId =
        "00000000-0000-0000-0000-000000000001";

    public async Task<FeedAutomationSimulationResult> SimulateAsync(
        FeedAutomationRuleDefinition definition,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        if (maximumEntries is < 1 or > MaximumEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                $"Simulation must inspect between 1 and {MaximumEntries} entries.");
        }

        FeedAutomationRuleDefinition normalized =
            FeedAutomationRuleValidator.ValidateAndNormalizeDefinition(definition);
        var rule = new FeedAutomationRule(
            SimulationRuleId,
            1,
            normalized.Name,
            normalized.Priority,
            normalized.ConflictOrder,
            normalized.IsEnabled,
            normalized.MatchMode,
            normalized.Conditions,
            normalized.Actions);
        FeedAutomationRuleSet ruleSet =
            FeedAutomationRuleInterpreter.Compile([rule]);

        Task<FeedEntryPage> entriesTask = entryRepository.QueryAsync(
            new(
                SearchText: null,
                FeedId: null,
                CategoryId: null,
                PublishedFrom: null,
                PublishedBefore: null,
                ReadFilter: FeedEntryReadFilter.All,
                Offset: 0,
                Limit: maximumEntries,
                ActiveOnly: false,
                FavoritesOnly: false,
                TagId: null,
                LocalProfile: "default",
                IncludeHidden: true),
            cancellationToken);
        Task<FeedCatalogSnapshot?> catalogTask =
            catalogRepository.GetCatalogAsync(
                FeedCatalogScope.All,
                cancellationToken);
        await Task.WhenAll(entriesTask, catalogTask).ConfigureAwait(false);

        FeedEntryPage page = await entriesTask.ConfigureAwait(false);
        FeedCatalogSnapshot? catalog = await catalogTask.ConfigureAwait(false);
        Dictionary<string, FeedCatalogItem> feeds =
            (catalog?.Feeds ?? Array.Empty<FeedCatalogItem>())
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var results = new List<FeedAutomationSimulationEntry>(page.Items.Count);
        int matchedCount = 0;
        foreach (FeedEntry entry in page.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            feeds.TryGetValue(entry.FeedId, out FeedCatalogItem? feed);
            FeedAutomationPlan plan = ruleSet.Plan(CreateContext(entry, feed));
            FeedAutomationRuleEvaluation evaluation =
                AssertSingleEvaluation(plan);
            if (evaluation.Outcome == FeedAutomationRuleEvaluationOutcome.Matched)
            {
                matchedCount++;
            }
            results.Add(new(
                entry.Id,
                entry.Title,
                feed?.DisplayName ?? "未知来源",
                entry.PublishedAt,
                evaluation.Outcome,
                plan.Actions));
        }

        return new(
            results.Count,
            matchedCount,
            results.AsReadOnly());
    }

    private static FeedAutomationRuleEvaluation AssertSingleEvaluation(
        FeedAutomationPlan plan)
    {
        if (plan.RuleEvaluations.Count != 1)
        {
            throw new InvalidOperationException(
                "Simulation produced an unexpected rule evaluation count.");
        }
        return plan.RuleEvaluations[0];
    }

    private static FeedAutomationEntryContext CreateContext(
        FeedEntry entry,
        FeedCatalogItem? feed)
    {
        string content = string.IsNullOrWhiteSpace(entry.SanitizedContent)
            ? entry.Summary
            : entry.SanitizedContent;
        content = TruncateContent(content);
        IReadOnlyList<FeedEnclosure> enclosures =
            entry.Enclosures ?? Array.Empty<FeedEnclosure>();
        bool hasAudio =
            feed?.ViewKind == FeedViewKind.Audio
            || enclosures.Any(enclosure =>
                IsMediaType(enclosure.MediaType, "audio/"));
        bool hasVideo =
            feed?.ViewKind == FeedViewKind.Video
            || enclosures.Any(enclosure =>
                IsMediaType(enclosure.MediaType, "video/"));
        return new(
            entry.Id,
            entry.FeedId,
            feed?.CategoryId,
            entry.Title,
            entry.Author,
            content,
            Language: null,
            entry.PublishedAt,
            hasAudio,
            hasVideo);
    }

    private static string TruncateContent(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length <= FeedAutomationRuleInterpreter.MaximumContentLength)
        {
            return value ?? string.Empty;
        }
        int length = FeedAutomationRuleInterpreter.MaximumContentLength;
        if (char.IsHighSurrogate(value[length - 1])
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }
        return value[..length];
    }

    private static bool IsMediaType(
        string? mediaType,
        string prefix) =>
        mediaType?.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase) == true;
}
