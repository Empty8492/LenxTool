using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class FeedAdminViewModel
{
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Status = "正在刷新共享目录…";
        try
        {
            await _catalogSync.SyncAsync(cancellationToken);
            await LoadCatalogAsync(null, cancellationToken);
            Status = $"共享目录 v{CatalogVersion} 已刷新。";
        }
        catch (AppException exception)
        {
            Status = $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
    }

    private void BeginNewCategory()
    {
        SelectedCategory = null;
        CategoryNameInput = string.Empty;
        CategorySortOrder = NextSortOrder(Categories.Select(category => category.SortOrder));
        CategoryIsEnabled = true;
        ResetCategoryAiPolicy();
        PendingDeleteCategoryId = null;
        Status = "正在新增分类；保存时会校验目录版本。";
    }

    private Task SaveCategoryAsync(CancellationToken cancellationToken)
    {
        var input = new FeedCategoryInput(
            CategoryNameInput.Trim(),
            CategorySortOrder,
            CategoryIsEnabled,
            CreateCategoryAiPolicy());
        return ExecuteMutationAsync(
            (version, token) => SelectedCategory is null
                ? _adminService.CreateCategoryAsync(input, version, token)
                : _adminService.UpdateCategoryAsync(SelectedCategory.Id, input, version, token),
            "分类已保存并同步。",
            cancellationToken);
    }

    private Task ToggleCategoryAsync(FeedCategory? category, CancellationToken cancellationToken) =>
        category is null
            ? Task.CompletedTask
            : ExecuteMutationAsync(
                (version, token) => _adminService.UpdateCategoryAsync(
                    category.Id,
                    new(
                        category.Name,
                        category.SortOrder,
                        !category.IsEnabled,
                        category.AiPolicy),
                    version,
                    token),
                category.IsEnabled ? "分类已停用。" : "分类已启用。",
                cancellationToken);

    private Task MoveCategoryAsync(FeedCategory? category, int direction, CancellationToken cancellationToken)
    {
        int index = IndexOf(Categories, category?.Id);
        if (index < 0 || index + direction < 0 || index + direction >= Categories.Count)
            return Task.CompletedTask;
        int sortOrder = OrderAround(Categories[index + direction].SortOrder, direction);
        return ExecuteMutationAsync(
            (version, token) => _adminService.UpdateCategoryAsync(
                category!.Id,
                new(category.Name, sortOrder, category.IsEnabled, category.AiPolicy),
                version,
                token),
            "分类排序已更新。",
            cancellationToken);
    }

    private void PrepareDeleteCategory(FeedCategory? category)
    {
        if (category is null) return;
        SelectedCategory = category;
        PendingDeleteCategoryId = category.Id;
        Status = $"再次确认将删除分类“{category.Name}”；含有 Feed 的分类会被服务端拒绝。";
    }

    private Task ConfirmDeleteCategoryAsync(CancellationToken cancellationToken)
    {
        string? id = PendingDeleteCategoryId;
        if (id is null) return Task.CompletedTask;
        return ExecuteMutationAsync(
            (version, token) => _adminService.DeleteCategoryAsync(id, version, token),
            "分类已删除；本地文章不会随目录删除。",
            cancellationToken);
    }

    private void CancelDeleteCategory()
    {
        PendingDeleteCategoryId = null;
        Status = "已取消删除分类。";
    }
}
