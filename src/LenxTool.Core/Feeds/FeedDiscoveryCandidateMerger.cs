using LenxTool.Core.Models;

namespace LenxTool.Core.Feeds;

public static class FeedDiscoveryCandidateMerger
{
    public static IReadOnlyList<FeedDiscoveryCandidate> Merge(
        IEnumerable<FeedDiscoveryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var mergedByUrl =
            new Dictionary<string, FeedDiscoveryCandidate>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (FeedDiscoveryCandidate candidate in candidates)
        {
            if (candidate.Evidence is null
                || candidate.Evidence.Count == 0
                || candidate.Warnings is null
                || !Enum.IsDefined(candidate.Health)
                || candidate.Evidence.Any(evidence =>
                    string.IsNullOrWhiteSpace(evidence.SourceId)
                    || evidence.SourceId.Length > 128
                    || evidence.SourceId.Any(char.IsControl)
                    || !Enum.IsDefined(evidence.SourceKind)
                    || !Enum.IsDefined(evidence.MatchKind)
                    || !Enum.IsDefined(evidence.Confidence))
                || candidate.Warnings.Any(warning =>
                    !Enum.IsDefined(warning.Code)
                    || (warning.SourceId is not null
                        && (string.IsNullOrWhiteSpace(warning.SourceId)
                            || warning.SourceId.Length > 128
                            || warning.SourceId.Any(char.IsControl)))))
            {
                throw new ArgumentException(
                    "Every discovery candidate must have valid typed source evidence.",
                    nameof(candidates));
            }
            if (!Uri.TryCreate(
                    candidate.NormalizedFeedUrl,
                    UriKind.Absolute,
                    out Uri? uri)
                || !FeedDiscoveryUrlNormalizer.TryNormalizeHttpUrl(
                    uri,
                    out string normalizedUrl))
            {
                throw new ArgumentException(
                    "Every discovery candidate must have a safe HTTP or HTTPS identity.",
                    nameof(candidates));
            }

            FeedDiscoveryCandidate normalized =
                candidate with { NormalizedFeedUrl = normalizedUrl };
            if (!mergedByUrl.TryGetValue(normalizedUrl, out FeedDiscoveryCandidate? current))
            {
                mergedByUrl.Add(normalizedUrl, normalized);
                order.Add(normalizedUrl);
                continue;
            }

            mergedByUrl[normalizedUrl] = MergePair(current, normalized);
        }

        return order.Select(url => mergedByUrl[url]).ToArray();
    }

    private static FeedDiscoveryCandidate MergePair(
        FeedDiscoveryCandidate current,
        FeedDiscoveryCandidate incoming)
    {
        FeedDiscoveryCandidate preferred =
            incoming.Confidence > current.Confidence ? incoming : current;
        FeedDiscoveryCandidate fallback =
            ReferenceEquals(preferred, incoming) ? current : incoming;

        FeedDiscoveryEvidence[] evidence = current.Evidence
            .Concat(incoming.Evidence)
            .Distinct()
            .ToArray();
        FeedDiscoveryWarning[] warnings = current.Warnings
            .Concat(incoming.Warnings)
            .Distinct()
            .ToArray();

        return preferred with
        {
            Title = preferred.Title ?? fallback.Title,
            SiteUrl = preferred.SiteUrl ?? fallback.SiteUrl,
            DocumentKind = preferred.DocumentKind ?? fallback.DocumentKind,
            LastUpdatedAt = Latest(current.LastUpdatedAt, incoming.LastUpdatedAt),
            Evidence = evidence,
            Warnings = warnings
        };
    }

    private static DateTimeOffset? Latest(
        DateTimeOffset? left,
        DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }
        if (right is null)
        {
            return left;
        }
        return left >= right ? left : right;
    }
}
