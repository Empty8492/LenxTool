using System.IO;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly IDatabaseMaintenanceService? _databaseMaintenance;
    private StorageCleanupPreview? _storageCleanupPreview;
    private string _databaseUsage = "尚未统计";
    private string _imageCacheUsage = "尚未统计";
    private string _modelUsage = "尚未统计";
    private string _storageStatus =
        "容量统计在主窗口显示后后台执行，不会阻塞启动。";
    private string _storageCleanupPreviewSummary = string.Empty;
    private bool _isStorageCleanupPreviewVisible;
    private bool _isStorageBusy;

    public string DatabaseUsage
    {
        get => _databaseUsage;
        private set => SetProperty(ref _databaseUsage, value);
    }

    public string ImageCacheUsage
    {
        get => _imageCacheUsage;
        private set => SetProperty(ref _imageCacheUsage, value);
    }

    public string ModelUsage
    {
        get => _modelUsage;
        private set => SetProperty(ref _modelUsage, value);
    }

    public string StorageStatus
    {
        get => _storageStatus;
        private set => SetProperty(ref _storageStatus, value);
    }

    public string StorageCleanupPreviewSummary
    {
        get => _storageCleanupPreviewSummary;
        private set => SetProperty(
            ref _storageCleanupPreviewSummary,
            value);
    }

    public bool IsStorageCleanupPreviewVisible
    {
        get => _isStorageCleanupPreviewVisible;
        private set => SetProperty(
            ref _isStorageCleanupPreviewVisible,
            value);
    }

    public bool IsStorageBusy
    {
        get => _isStorageBusy;
        private set => SetProperty(ref _isStorageBusy, value);
    }

    public AsyncRelayCommand RefreshStorageUsageCommand { get; private set; } =
        null!;
    public AsyncRelayCommand PreviewStorageCleanupCommand
    { get; private set; } = null!;
    public AsyncRelayCommand ConfirmStorageCleanupCommand
    { get; private set; } = null!;
    public RelayCommand CancelStorageCleanupPreviewCommand
    { get; private set; } = null!;

    public async Task RefreshStorageUsageInBackgroundAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await RefreshStorageUsageAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown can cancel a background size scan.
        }
    }

    private void ConfigureStorageMaintenance()
    {
        RefreshStorageUsageCommand = new(
            RefreshStorageUsageAsync,
            CanStartStorageOperation);
        PreviewStorageCleanupCommand = new(
            PreviewStorageCleanupAsync,
            CanStartStorageOperation);
        ConfirmStorageCleanupCommand = new(
            ConfirmStorageCleanupAsync,
            () => CanStartStorageOperation()
                && _storageCleanupPreview is not null);
        CancelStorageCleanupPreviewCommand = new(
            CancelStorageCleanupPreview,
            () => _storageCleanupPreview is not null);
    }

    private async Task RefreshStorageUsageAsync(
        CancellationToken cancellationToken)
    {
        if (_databaseMaintenance is null)
        {
            StorageStatus = "本地存储维护服务不可用。";
            return;
        }
        SetStorageBusy(true);
        StorageStatus = "正在后台统计数据库、图片缓存和模型占用…";
        try
        {
            LocalStorageUsage usage =
                await _databaseMaintenance.GetStorageUsageAsync(
                    cancellationToken);
            ApplyStorageUsage(usage);
            StorageStatus = $"当前本地占用合计 {FormatSize(usage.TotalBytes)}。";
        }
        catch (OperationCanceledException)
        {
            StorageStatus = "容量统计已取消。";
            throw;
        }
        catch (AppException exception)
        {
            StorageStatus =
                $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            StorageStatus = "暂时无法读取本地占用；请检查当前用户目录权限。";
        }
        finally
        {
            SetStorageBusy(false);
        }
    }

    private async Task PreviewStorageCleanupAsync(
        CancellationToken cancellationToken)
    {
        if (_databaseMaintenance is null)
        {
            return;
        }
        SetStorageBusy(true);
        StorageStatus = "正在预览 180 天保留策略，不会删除任何内容…";
        try
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(
                -StorageRetentionPolicy.DefaultDays);
            _storageCleanupPreview =
                await _databaseMaintenance.PreviewCleanupAsync(
                    cutoff,
                    cancellationToken);
            StorageCleanupPreviewSummary =
                $"预计清理 {_storageCleanupPreview.ExpiredFeedEntryCount} 条" +
                $" 180 天前且无收藏、备注、标签或活动任务引用的内容；" +
                $"预计可回收 {_storageCleanupPreview.ReclaimableImageFileCount}" +
                $" 个图片文件（{FormatSize(_storageCleanupPreview.ReclaimableImageBytes)}）。";
            IsStorageCleanupPreviewVisible = true;
            StorageStatus =
                "预览完成；核对后点击“确认清理”，或取消返回。";
        }
        catch (OperationCanceledException)
        {
            StorageStatus = "清理预览已取消。";
            throw;
        }
        catch (AppException exception)
        {
            StorageStatus =
                $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            StorageStatus = "暂时无法生成清理预览；本地内容未被修改。";
        }
        finally
        {
            SetStorageBusy(false);
            NotifyStorageCommands();
        }
    }

    private async Task ConfirmStorageCleanupAsync(
        CancellationToken cancellationToken)
    {
        if (_databaseMaintenance is null
            || _storageCleanupPreview is null)
        {
            return;
        }
        StorageCleanupPreview preview = _storageCleanupPreview;
        SetStorageBusy(true);
        StorageStatus = "正在执行安全清理；可点击取消停止后续批次…";
        try
        {
            StorageCleanupResult result =
                await _databaseMaintenance.RunCleanupAsync(
                    preview.Cutoff,
                    cancellationToken);
            ApplyStorageUsage(result.Usage);
            StorageStatus =
                $"已清理 {result.DeletedFeedEntryCount} 条过期内容，" +
                $"回收 {result.RemovedImageFileCount} 个图片文件" +
                $"（{FormatSize(result.ReclaimedImageBytes)}）" +
                (result.DatabaseOptimized
                    ? "，数据库维护已完成。"
                    : "；数据已清理，数据库压缩因空间不足或占用而跳过。");
            ClearStorageCleanupPreview();
        }
        catch (OperationCanceledException)
        {
            StorageStatus =
                "清理已停止；已完成的安全批次会保留，可重新预览剩余内容。";
            throw;
        }
        catch (AppException exception)
        {
            StorageStatus =
                $"{exception.Error.Title}：{exception.Error.Suggestion}";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            StorageStatus =
                "清理未能全部完成；请检查磁盘空间和当前用户目录权限。";
        }
        finally
        {
            SetStorageBusy(false);
            NotifyStorageCommands();
        }
    }

    private void CancelStorageCleanupPreview()
    {
        if (IsStorageBusy)
        {
            ConfirmStorageCleanupCommand.Cancel();
            PreviewStorageCleanupCommand.Cancel();
            StorageStatus = "正在停止存储维护…";
            return;
        }
        ClearStorageCleanupPreview();
        StorageStatus = "已取消清理预览；本地内容未被修改。";
    }

    private void ClearStorageCleanupPreview()
    {
        _storageCleanupPreview = null;
        StorageCleanupPreviewSummary = string.Empty;
        IsStorageCleanupPreviewVisible = false;
        NotifyStorageCommands();
    }

    private void ApplyStorageUsage(LocalStorageUsage usage)
    {
        DatabaseUsage = FormatSize(usage.DatabaseBytes);
        ImageCacheUsage =
            $"{FormatSize(usage.ImageCacheBytes)} · " +
            $"{usage.ImageFileCount} 个文件";
        ModelUsage =
            $"{FormatSize(usage.ModelBytes)} · " +
            $"{usage.ModelFileCount} 个文件";
    }

    private bool CanStartStorageOperation() =>
        _databaseMaintenance is not null && !IsStorageBusy;

    private void SetStorageBusy(bool value)
    {
        IsStorageBusy = value;
        NotifyStorageCommands();
    }

    private void NotifyStorageCommands()
    {
        RefreshStorageUsageCommand.NotifyCanExecuteChanged();
        PreviewStorageCleanupCommand.NotifyCanExecuteChanged();
        ConfirmStorageCleanupCommand.NotifyCanExecuteChanged();
        CancelStorageCleanupPreviewCommand.NotifyCanExecuteChanged();
    }
}
