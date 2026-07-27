using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class FeedAutomationPlanningService(
    IFeedAutomationRuleRepository ruleRepository,
    IFeedAutomationRunRepository runRepository,
    TimeProvider timeProvider)
    : IFeedAutomationPlanningService
{
    private const int MaximumEntriesPerBatch = 5_000;

    public async Task<FeedAutomationPlanningResult> StageAsync(
        FeedCatalogItem feed,
        IReadOnlyList<FeedEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(entries);
        ValidateEntries(feed, entries);

        FeedAutomationRuleSnapshot snapshot =
            await ruleRepository.GetAsync(cancellationToken)
                .ConfigureAwait(false);
        if (snapshot.Rules.Count == 0)
        {
            return new(
                snapshot.RuleSetVersion,
                0,
                0,
                0);
        }

        FeedAutomationRuleSet ruleSet =
            FeedAutomationRuleInterpreter.Compile(snapshot.Rules);
        DateTimeOffset stagedAt = timeProvider.GetUtcNow();
        int ruleRunsCreated = 0;
        int actionRunsCreated = 0;
        foreach (FeedEntry entry in entries)
        {
            FeedAutomationPlan plan = ruleSet.Plan(
                CreateContext(feed, entry));
            FeedAutomationStageResult staged =
                await runRepository.StageAsync(
                        plan,
                        stagedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
            ruleRunsCreated += staged.RuleRunsCreated;
            actionRunsCreated += staged.ActionRunsCreated;
        }

        return new(
            snapshot.RuleSetVersion,
            entries.Count,
            ruleRunsCreated,
            actionRunsCreated);
    }

    private static void ValidateEntries(
        FeedCatalogItem feed,
        IReadOnlyList<FeedEntry> entries)
    {
        if (!Guid.TryParseExact(feed.Id, "D", out _))
        {
            throw new ArgumentException(
                "Feed ID must be a canonical GUID.",
                nameof(feed));
        }
        if (entries.Count > MaximumEntriesPerBatch)
        {
            throw new ArgumentException(
                "Automation entry count exceeds the local planning limit.",
                nameof(entries));
        }
        for (int index = 0; index < entries.Count; index++)
        {
            FeedEntry? entry = entries[index];
            if (entry is null
                || !string.Equals(
                    entry.FeedId,
                    feed.Id,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Automation entries must belong to the supplied Feed.",
                    nameof(entries));
            }
        }
    }

    private static FeedAutomationEntryContext CreateContext(
        FeedCatalogItem feed,
        FeedEntry entry)
    {
        string content = string.IsNullOrWhiteSpace(
            entry.SanitizedContent)
            ? entry.Summary
            : entry.SanitizedContent;
        content = TruncateContent(content);
        IReadOnlyList<FeedEnclosure> enclosures =
            entry.Enclosures ?? Array.Empty<FeedEnclosure>();
        bool hasAudio =
            (feed.IsViewKindExplicit && feed.ViewKind == FeedViewKind.Audio)
            || enclosures.Any(
                enclosure => IsMediaType(
                    enclosure.MediaType,
                    "audio/"));
        bool hasVideo =
            (feed.IsViewKindExplicit && feed.ViewKind == FeedViewKind.Video)
            || enclosures.Any(
                enclosure => IsMediaType(
                    enclosure.MediaType,
                    "video/"));
        return new(
            entry.Id,
            feed.Id,
            feed.CategoryId,
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
            || value.Length
                <= FeedAutomationRuleInterpreter.MaximumContentLength)
        {
            return value ?? string.Empty;
        }

        int length =
            FeedAutomationRuleInterpreter.MaximumContentLength;
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
