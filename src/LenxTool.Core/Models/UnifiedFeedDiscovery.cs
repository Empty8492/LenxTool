namespace LenxTool.Core.Models;

public enum FeedDiscoveryQueryKind
{
    Invalid,
    Url,
    RssHubRoute,
    Keyword
}

public enum FeedDiscoveryQueryError
{
    None,
    Empty,
    TooLong,
    ControlCharacter,
    UnsupportedScheme,
    InvalidUrl,
    InvalidRssHubRoute,
    CredentialsNotAllowed,
    FragmentNotAllowed
}

public sealed record FeedDiscoveryQuery(
    string? NormalizedValue,
    FeedDiscoveryQueryKind Kind,
    FeedDiscoveryQueryError Error)
{
    public bool IsValid =>
        Kind != FeedDiscoveryQueryKind.Invalid
        && Error == FeedDiscoveryQueryError.None
        && NormalizedValue is not null;
}

public enum FeedDiscoverySourceKind
{
    KnownCatalog,
    DirectProbe,
    RssHubAdapter,
    ExternalProvider
}

public enum FeedDiscoveryMatchKind
{
    ExactFeedUrl,
    ExactSiteUrl,
    ExactTitle,
    Keyword,
    Redirect
}

public enum FeedDiscoveryConfidence
{
    Low = 1,
    Medium = 2,
    High = 3,
    Exact = 4
}

public enum FeedDiscoveryHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unavailable
}

public enum FeedDiscoveryWarningCode
{
    Stale,
    InsecureTransport,
    Unverified,
    ProviderPartialFailure,
    RateLimited
}

public sealed record FeedDiscoveryEvidence(
    string SourceId,
    FeedDiscoverySourceKind SourceKind,
    FeedDiscoveryMatchKind MatchKind,
    FeedDiscoveryConfidence Confidence);

public sealed record FeedDiscoveryWarning(
    FeedDiscoveryWarningCode Code,
    string? SourceId);

public sealed record FeedDiscoveryCandidate(
    string NormalizedFeedUrl,
    string? Title,
    string? SiteUrl,
    FeedDocumentKind? DocumentKind,
    DateTimeOffset? LastUpdatedAt,
    FeedDiscoveryHealth Health,
    IReadOnlyList<FeedDiscoveryEvidence> Evidence,
    IReadOnlyList<FeedDiscoveryWarning> Warnings)
{
    public FeedDiscoveryConfidence Confidence =>
        Evidence.Count == 0
            ? FeedDiscoveryConfidence.Low
            : Evidence.Max(evidence => evidence.Confidence);
}
