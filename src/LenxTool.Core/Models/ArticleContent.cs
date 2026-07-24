namespace LenxTool.Core.Models;

public sealed record ArticleContentResult(
    string RequestedUrl,
    string FinalUrl,
    string? Title,
    string? Author,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ArticleContentBlock> Blocks,
    IReadOnlyList<ArticleExtractionWarning> Warnings,
    string ExtractorVersion);

public sealed record ArticleContentBlock(
    ArticleContentBlockKind Kind,
    string Text,
    string? ResourceUrl,
    int? HeadingLevel,
    IReadOnlyList<ArticleContentLink> Links);

public sealed record ArticleContentLink(
    string Url,
    string Text);

public sealed record ArticleExtractionWarning(
    ArticleExtractionWarningCode Code,
    string Message);

public enum ArticleContentBlockKind
{
    Heading,
    Paragraph,
    ListItem,
    Quote,
    Image
}

public enum ArticleExtractionWarningCode
{
    NoReadableContent,
    InvalidMetadata,
    EncodingFallback,
    BlockLimitReached,
    TextLimitReached
}
