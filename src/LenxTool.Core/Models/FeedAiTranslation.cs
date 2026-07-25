namespace LenxTool.Core.Models;

public enum FeedAiTranslationBlockKind
{
    Title,
    Summary,
    Heading,
    Paragraph,
    ListItem,
    Quote
}

/// <summary>
/// 可翻译的纯文本块。链接和资源地址仅作为本地展示元数据保留，不发送给模型。
/// </summary>
public sealed record FeedAiTranslationBlock(
    int Sequence,
    FeedAiTranslationBlockKind Kind,
    string Text,
    string? ResourceUrl,
    int? HeadingLevel,
    IReadOnlyList<ArticleContentLink> Links);

public sealed record FeedAiTranslationInput(
    string EntryId,
    string ContentHash,
    string Title,
    string TargetLanguage,
    IReadOnlyList<FeedAiTranslationBlock> Blocks);

public sealed record FeedAiTranslatedBlock(
    int Sequence,
    FeedAiTranslationBlockKind Kind,
    string OriginalText,
    string TranslatedText,
    string? ResourceUrl,
    int? HeadingLevel,
    IReadOnlyList<ArticleContentLink> Links);

public sealed record FeedAiTranslationResult(
    FeedAiResult CacheRecord,
    IReadOnlyList<FeedAiTranslatedBlock> Blocks);

public sealed record FeedAiTranslationOptions(
    string Model,
    string PromptVersion,
    int BatchSize,
    int MaximumBlocks,
    int MaximumBlockCharacters,
    int MaximumTotalCharacters)
{
    public static FeedAiTranslationOptions Default { get; } = new(
        "deepseek-v4-flash",
        "feed-translation-v1",
        20,
        2_000,
        12_000,
        2_000_000);
}
