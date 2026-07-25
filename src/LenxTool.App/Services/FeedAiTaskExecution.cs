using LenxTool.App.Controls;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

internal static class FeedAiTaskExecution
{
    public static async Task ExecuteAsync(
        FeedEntry entry,
        FeedAiAutomationTaskType taskType,
        string targetLanguage,
        IFeedAiSummaryService summaryService,
        IFeedAiTranslationService translationService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(summaryService);
        ArgumentNullException.ThrowIfNull(translationService);

        string sourceContent = string.IsNullOrWhiteSpace(entry.SanitizedContent)
            ? entry.Summary
            : entry.SanitizedContent;
        RichArticleDocument document = RichArticleFormatter.Parse(
            sourceContent,
            entry.NormalizedUrl);
        RichArticleTranslationSource source =
            RichArticleFormatter.CreateTranslationSource(
                document,
                entry.Title);

        if (taskType == FeedAiAutomationTaskType.Summary)
        {
            string summarySource = string.Join(
                Environment.NewLine,
                source.Blocks
                    .Where(block =>
                        block.Kind != FeedAiTranslationBlockKind.Title)
                    .Select(block => block.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            if (string.IsNullOrWhiteSpace(summarySource))
            {
                summarySource = string.IsNullOrWhiteSpace(entry.Summary)
                    ? entry.Title
                    : entry.Summary;
            }

            await summaryService.SummarizeAsync(
                new(
                    entry.Id,
                    entry.ContentHash,
                    entry.Title,
                    summarySource),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (taskType != FeedAiAutomationTaskType.Translation)
        {
            throw new ArgumentOutOfRangeException(nameof(taskType));
        }

        await translationService.TranslateAsync(
            new(
                entry.Id,
                entry.ContentHash,
                entry.Title,
                targetLanguage,
                source.Blocks),
            cancellationToken).ConfigureAwait(false);
    }
}
