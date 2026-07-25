using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface IFeedAiTranslationService
{
    Task<FeedAiTranslationResult> TranslateAsync(
        FeedAiTranslationInput input,
        CancellationToken cancellationToken);
}
