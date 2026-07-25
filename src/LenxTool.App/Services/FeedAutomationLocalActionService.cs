using System.IO;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.Services;

public sealed class FeedAutomationLocalActionService(
    IFeedEntryRepository entries,
    IEntryStateRepository entryStates,
    IFavoriteRepository favorites)
    : IFeedAutomationLocalActionService
{
    private const int MaximumEntryIdLength = 128;
    internal const string LocalProfile = "default";
    internal const string AutomationTagColor = "#4B6B88";

    public async Task<FeedAutomationLocalActionResult> ExecuteAsync(
        FeedAutomationActionLease action,
        CancellationToken cancellationToken)
    {
        ValidateAction(action);
        FeedEntry? entry = await entries.GetByIdAsync(
            action.EntryId,
            cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return FeedAutomationLocalActionResult.EntryMissing;
        }

        switch (action.Type)
        {
            case FeedAutomationActionType.AddTag:
                await favorites.AddTagAsync(
                    "feed_entry",
                    entry.Id,
                    action.Value!,
                    AutomationTagColor,
                    cancellationToken).ConfigureAwait(false);
                break;
            case FeedAutomationActionType.Hide:
                await entryStates.PatchAsync(
                    entry.Id,
                    LocalProfile,
                    new(IsHidden: true),
                    cancellationToken).ConfigureAwait(false);
                break;
            case FeedAutomationActionType.MarkRead:
                await entryStates.PatchAsync(
                    entry.Id,
                    LocalProfile,
                    new(IsRead: true),
                    cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException(
                    "The automation action is not a local state action.");
        }

        return FeedAutomationLocalActionResult.Completed;
    }

    private static void ValidateAction(FeedAutomationActionLease action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Type is not (
                FeedAutomationActionType.AddTag
                or FeedAutomationActionType.Hide
                or FeedAutomationActionType.MarkRead))
        {
            throw new InvalidOperationException(
                "The automation action is not a local state action.");
        }
        if (string.IsNullOrWhiteSpace(action.EntryId)
            || action.EntryId.Length > MaximumEntryIdLength
            || action.EntryId.Any(char.IsControl)
            || !string.Equals(
                action.EntryId,
                action.EntryId.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The automation action entry identity is invalid.");
        }
        if (action.Type == FeedAutomationActionType.AddTag)
        {
            if (string.IsNullOrWhiteSpace(action.Value)
                || action.Value.Length > 80
                || action.Value.Any(char.IsControl)
                || !string.Equals(
                    action.Value,
                    action.Value.Trim(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The automation tag action value is invalid.");
            }
        }
        else if (action.Value is not null)
        {
            throw new InvalidDataException(
                "This local automation action cannot contain a value.");
        }
    }
}
