using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class FeedDiscoveryViewModel
{
    private void ApplyPublishingCatalog(FeedCatalogSnapshot? snapshot)
    {
        if (snapshot?.State.Scope != FeedCatalogScope.All)
        {
            ClearPublishingCatalog();
            return;
        }
        _publishingCatalog = snapshot;
        OnPropertyChanged(nameof(CatalogVersion));
        RebuildPublishCategories(snapshot);
        IsCatalogCurrent = true;
    }

    private void ClearPublishingCatalog()
    {
        _publishingCatalog = null;
        OnPropertyChanged(nameof(CatalogVersion));
        RebuildPublishCategories(null);
        IsCatalogCurrent = false;
    }

    private void InvalidatePublishingCatalog()
    {
        IsCatalogCurrent = false;
        IsPublishConfirmed = false;
    }

    private void RebuildPublishCategories(
        FeedCatalogSnapshot? snapshot)
    {
        string? selectedId = SelectedPublishCategory?.Id;
        PublishCategories.Clear();
        PublishCategories.Add(new(null, "未分类"));
        if (snapshot is not null)
        {
            foreach (FeedCategory category in snapshot.Categories)
            {
                PublishCategories.Add(new(
                    category.Id,
                    category.IsEnabled
                        ? category.Name
                        : $"{category.Name}（已停用）"));
            }
        }
        _selectedPublishCategory = FindPublishCategory(selectedId);
        OnPropertyChanged(nameof(SelectedPublishCategory));
        OnPropertyChanged(nameof(PublishCategoryText));
    }

    private void RefreshCandidateCatalogMatches()
    {
        Dictionary<string, FeedCatalogItem> feeds =
            _publishingCatalog?.Feeds.ToDictionary(
                item => item.NormalizedUrl,
                StringComparer.Ordinal)
            ?? new(StringComparer.Ordinal);
        string? selectedUrl = SelectedPublishCandidate?.FeedUrl;
        for (int index = 0; index < Candidates.Count; index++)
        {
            FeedDiscoveryCandidateViewModel current = Candidates[index];
            Candidates[index] = current with
            {
                ExistingFeed = feeds.GetValueOrDefault(current.FeedUrl)
            };
        }
        SelectedPublishCandidate = Candidates.FirstOrDefault(item =>
            string.Equals(
                item.FeedUrl,
                selectedUrl,
                StringComparison.Ordinal));
        if (SelectedPublishCandidate is not null)
            PreparePublish(SelectedPublishCandidate);
    }

    private FeedPublishCategoryChoice FindPublishCategory(string? id) =>
        PublishCategories.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal))
        ?? PublishCategories[0];

    private FeedPublishViewChoice FindPublishView(
        FeedCatalogItem? existing) =>
        existing is null || !existing.IsViewKindExplicit
            ? PublishViewChoices[0]
            : PublishViewChoices.Single(item =>
                item.Kind == existing.ViewKind);

    private void InvalidatePublishConfirmation()
    {
        if (IsPublishConfirmed) IsPublishConfirmed = false;
        OnPropertyChanged(nameof(PublishValidationText));
        NotifyPublishingCommands();
    }

    private void ResetPublishingSelection()
    {
        SelectedPublishCandidate = null;
        IsPublishConfirmed = false;
        NotifyPublishingCommands();
    }

    private void NotifyPublishSummary()
    {
        OnPropertyChanged(nameof(HasPublishSelection));
        OnPropertyChanged(nameof(IsExistingSelection));
        OnPropertyChanged(nameof(ShowPublishConfirmation));
        OnPropertyChanged(nameof(CanEditPublishPolicy));
        OnPropertyChanged(nameof(PublishPanelTitle));
        OnPropertyChanged(nameof(PublishNormalizedUrl));
        OnPropertyChanged(nameof(PublishCategoryText));
        OnPropertyChanged(nameof(PublishRefreshText));
        OnPropertyChanged(nameof(PublishViewText));
        OnPropertyChanged(nameof(PublishFullTextText));
        OnPropertyChanged(nameof(PublishValidationText));
        NotifyPublishingCommands();
    }

    private void NotifyPublishingCommands()
    {
        PreparePublishCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
        CancelPublishCommand.NotifyCanExecuteChanged();
        RefreshCatalogCommand.NotifyCanExecuteChanged();
    }
}
