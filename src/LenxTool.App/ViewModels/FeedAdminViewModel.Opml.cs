using System.ComponentModel;
using System.Text;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class FeedAdminViewModel
{
    private long? _opmlPreviewCatalogVersion;
    private bool _isOpmlBusy;

    private Task PreviewOpmlAsync(CancellationToken cancellationToken) =>
        RunOpmlOperationAsync(PreviewOpmlCoreAsync, cancellationToken);

    private async Task PreviewOpmlCoreAsync(CancellationToken cancellationToken)
    {
        string? path = _opmlFileDialogs.PickOpmlImport();
        if (path is null) return;
        Status = "正在安全读取 OPML 并生成预览…";
        try
        {
            OpmlDocument document = await _opmlFileService.LoadAsync(path, cancellationToken);
            FeedCatalogSnapshot? snapshot = await _repository.GetCatalogAsync(
                FeedCatalogScope.All,
                cancellationToken);
            if (!CanManage || snapshot is null || snapshot.State.Version != CatalogVersion)
            {
                ClearOpmlPreview();
                Status = "目录状态已变化，请刷新后重新选择 OPML 文件。";
                return;
            }

            ReplaceOpmlItems(OpmlCatalogPlanner.CreatePreview(document, snapshot));
            _opmlPreviewCatalogVersion = CatalogVersion;
            NotifyOpmlCommands();
            Status = $"OPML 预览已生成；{OpmlSummary}";
        }
        catch (AppException exception)
        {
            ClearOpmlPreview();
            Status = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
    }

    private Task ImportSelectedOpmlAsync(CancellationToken cancellationToken) =>
        !CanImportSelectedOpml()
            ? Task.CompletedTask
            : RunOpmlOperationAsync(ImportSelectedOpmlCoreAsync, cancellationToken);

    private async Task ImportSelectedOpmlCoreAsync(CancellationToken cancellationToken)
    {
        OpmlImportItemViewModel[] selected = OpmlItems.Where(item => item.IsSelected).ToArray();
        int projectedOperations = selected.Length + selected
            .Where(item => item.CategoryId is null && item.CategoryName is not null)
            .Select(item => NormalizeCategoryName(item.CategoryName!))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (projectedOperations > 100)
        {
            Status = $"所选内容需要 {projectedOperations} 个批量操作，超过单批 100 项上限；请减少选择。";
            return;
        }
        Status = $"正在逐项安全验证 {selected.Length} 个订阅；验证完成前不会提交…";
        bool discoveryFailed = false;
        foreach (OpmlImportItemViewModel item in selected)
        {
            if (!CanManage || _opmlPreviewCatalogVersion != CatalogVersion)
            {
                Status = "目录或管理员会话已变化，已停止导入；请重新预览。";
                NotifyOpmlCommands();
                return;
            }
            try
            {
                FeedDiscoveryResult discovery = await _discoveryService.DiscoverAsync(
                    item.FeedUrl,
                    cancellationToken);
                if (discovery.Feeds.Count == 0)
                {
                    item.RejectDiscovery("安全发现没有返回可用订阅。");
                    discoveryFailed = true;
                    continue;
                }
                DiscoveredFeed feed = discovery.Feeds[0];
                if (!IsValidHttpsUrl(feed.FeedUrl))
                {
                    item.RejectDiscovery("安全发现结果不符合 Worker 的 HTTPS 契约。");
                    discoveryFailed = true;
                    continue;
                }
                item.ApplyDiscovery(feed);
            }
            catch (AppException exception)
            {
                item.RejectDiscovery($"安全验证失败：{exception.Error.Suggestion}");
                discoveryFailed = true;
            }
        }
        if (discoveryFailed)
        {
            NotifyOpmlPreviewChanged();
            Status = "至少一项未通过安全验证，本次未提交任何目录更改；请检查预览后重试。";
            return;
        }

        FeedCatalogSnapshot? current = await _repository.GetCatalogAsync(
            FeedCatalogScope.All,
            cancellationToken);
        if (!CanManage
            || current is null
            || current.State.Version != CatalogVersion
            || _opmlPreviewCatalogVersion != CatalogVersion)
        {
            Status = "目录状态已变化，本次未提交；请重新预览。";
            return;
        }
        var recheckDocument = new OpmlDocument(
            "OPML 导入复核",
            selected.Select(item => new OpmlFeed(
                item.Title,
                item.FeedUrl,
                item.SiteUrl,
                item.CategoryName is null ? [] : [item.CategoryName])).ToArray());
        IReadOnlyList<OpmlCatalogPreviewItem> rechecked = OpmlCatalogPlanner.CreatePreview(recheckDocument, current);
        if (rechecked.Any(item => item.Status != OpmlCatalogItemStatus.New))
        {
            for (int index = 0; index < selected.Length; index++)
            {
                if (rechecked[index].Status != OpmlCatalogItemStatus.New)
                    selected[index].RejectDiscovery($"安全复核未通过：{rechecked[index].Message}");
            }
            NotifyOpmlPreviewChanged();
            Status = "安全发现后的地址出现重复或冲突，本次未提交；请检查预览。";
            return;
        }

        List<FeedCatalogBatchOperation> operations = BuildOpmlBatch(selected);
        if (operations.Count > 100)
        {
            Status = $"所选内容需要 {operations.Count} 个批量操作，超过单批 100 项上限；请减少选择。";
            return;
        }

        Status = $"正在原子提交 {operations.Count} 个目录操作…";
        FeedCatalogBatchResult result;
        try
        {
            result = await _batchService.ApplyAsync(operations, CatalogVersion, cancellationToken);
        }
        catch (AppException exception) when (IsCatalogVersionConflict(exception))
        {
            await RefreshAfterConflictAsync(cancellationToken);
            ClearOpmlPreview();
            return;
        }
        catch (AppException exception)
        {
            Status = $"{exception.Error.Title}：{exception.Error.Suggestion}";
            return;
        }

        try
        {
            await _catalogSync.SyncAsync(cancellationToken);
            await LoadCatalogAsync(result.CatalogVersion, cancellationToken);
            ClearOpmlPreview();
            if (_catalogIsCurrent) Status = $"已原子导入 {selected.Length} 个订阅，目录更新为 v{result.CatalogVersion}。";
        }
        catch (AppException exception)
        {
            _catalogIsCurrent = false;
            NotifyAllCommands();
            Status = $"远端导入已提交为 v{result.CatalogVersion}，但本地刷新失败：{exception.Error.Suggestion}";
        }
    }

    private Task ExportOpmlAsync(CancellationToken cancellationToken) =>
        RunOpmlOperationAsync(ExportOpmlCoreAsync, cancellationToken);

    private async Task ExportOpmlCoreAsync(CancellationToken cancellationToken)
    {
        string? path = _opmlFileDialogs.PickOpmlExport($"LenxTool-feeds-v{CatalogVersion}.opml");
        if (path is null) return;
        var categoryNames = Categories.ToDictionary(category => category.Id, category => category.Name, StringComparer.Ordinal);
        var document = new OpmlDocument(
            $"LenxTool 共享订阅 v{CatalogVersion}",
            Feeds.Select(feed => new OpmlFeed(
                feed.DisplayName,
                feed.OriginalUrl,
                feed.SiteUrl,
                feed.CategoryId is not null && categoryNames.TryGetValue(feed.CategoryId, out string? name)
                    ? [name]
                    : [])).ToArray());
        Status = "正在导出不含账号、凭据和本地抓取状态的 OPML…";
        try
        {
            await _opmlFileService.SaveAsync(path, document, cancellationToken);
            Status = $"已导出 {document.Feeds.Count} 个共享订阅。";
        }
        catch (AppException exception)
        {
            Status = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
    }

    private List<FeedCatalogBatchOperation> BuildOpmlBatch(
        OpmlImportItemViewModel[] selected)
    {
        var operations = new List<FeedCatalogBatchOperation>();
        var categoryOperations = new Dictionary<string, string>(StringComparer.Ordinal);
        int categorySortOrder = NextSortOrder(Categories.Select(category => category.SortOrder));
        foreach (OpmlImportItemViewModel item in selected)
        {
            if (item.CategoryId is not null || item.CategoryName is null) continue;
            string key = NormalizeCategoryName(item.CategoryName);
            if (categoryOperations.ContainsKey(key)) continue;
            string operationId = $"category-{categoryOperations.Count + 1}";
            categoryOperations.Add(key, operationId);
            operations.Add(new(
                operationId,
                FeedCatalogBatchOperationType.CreateCategory,
                CategoryInput: new(item.CategoryName, categorySortOrder, true)));
            categorySortOrder = Math.Min(MaximumSortOrder, categorySortOrder + 100);
        }

        int feedSortOrder = NextSortOrder(Feeds.Select(feed => feed.SortOrder));
        for (int index = 0; index < selected.Length; index++)
        {
            OpmlImportItemViewModel item = selected[index];
            string? categoryOperationId = item.CategoryId is null && item.CategoryName is not null
                ? categoryOperations[NormalizeCategoryName(item.CategoryName)]
                : null;
            operations.Add(new(
                $"feed-{index + 1}",
                FeedCatalogBatchOperationType.CreateFeed,
                FeedInput: new(
                    item.FeedUrl,
                    item.Title,
                    item.SiteUrl,
                    item.CategoryId,
                    FeedViewKind.Article,
                    60,
                    feedSortOrder,
                    true),
                CategoryOperationId: categoryOperationId));
            feedSortOrder = Math.Min(MaximumSortOrder, feedSortOrder + 100);
        }
        return operations;
    }

    private void SelectAllNewOpml()
    {
        foreach (OpmlImportItemViewModel item in OpmlItems) item.IsSelected = item.IsSelectable;
        NotifyOpmlPreviewChanged();
    }

    private void ClearOpmlSelection()
    {
        foreach (OpmlImportItemViewModel item in OpmlItems) item.IsSelected = false;
        NotifyOpmlPreviewChanged();
    }

    private bool CanImportSelectedOpml() => CanManage
        && !_isOpmlBusy
        && _opmlPreviewCatalogVersion == CatalogVersion
        && OpmlItems.Any(item => item.IsSelected && item.IsSelectable);

    private async Task RunOpmlOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (_isOpmlBusy) return;
        _isOpmlBusy = true;
        NotifyOpmlCommands();
        try
        {
            await operation(cancellationToken);
        }
        finally
        {
            _isOpmlBusy = false;
            NotifyOpmlCommands();
        }
    }

    private void ReplaceOpmlItems(IEnumerable<OpmlCatalogPreviewItem> items)
    {
        ClearOpmlPreview();
        foreach (OpmlCatalogPreviewItem item in items)
        {
            var viewModel = new OpmlImportItemViewModel(item);
            viewModel.PropertyChanged += OnOpmlItemPropertyChanged;
            OpmlItems.Add(viewModel);
        }
        NotifyOpmlPreviewChanged();
    }

    private void ClearOpmlPreview()
    {
        foreach (OpmlImportItemViewModel item in OpmlItems)
            item.PropertyChanged -= OnOpmlItemPropertyChanged;
        OpmlItems.Clear();
        _opmlPreviewCatalogVersion = null;
        NotifyOpmlPreviewChanged();
    }

    private void OnOpmlItemPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(OpmlImportItemViewModel.IsSelected))
            NotifyOpmlPreviewChanged();
    }

    private void NotifyOpmlPreviewChanged()
    {
        OnPropertyChanged(nameof(HasOpmlPreview));
        OnPropertyChanged(nameof(SelectedOpmlCount));
        OnPropertyChanged(nameof(OpmlSummary));
        NotifyOpmlCommands();
    }

    private void NotifyOpmlCommands()
    {
        PreviewOpmlCommand.NotifyCanExecuteChanged();
        ImportSelectedOpmlCommand.NotifyCanExecuteChanged();
        SelectAllNewOpmlCommand.NotifyCanExecuteChanged();
        ClearOpmlSelectionCommand.NotifyCanExecuteChanged();
        ExportOpmlCommand.NotifyCanExecuteChanged();
    }

    private static string NormalizeCategoryName(string value) =>
        value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
}
