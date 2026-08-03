using System.Globalization;
using System.IO;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.App.ViewModels;

/// <summary>
/// 为设置页提供稳定的 Zotero 条目类型名称，不把第三方 wire 值直接暴露给界面。
/// </summary>
public sealed record ZoteroItemTypeChoice(
    ZoteroItemType Value,
    string Label)
{
    public static IReadOnlyList<ZoteroItemTypeChoice> All { get; } =
    [
        new(ZoteroItemType.Webpage, "网页（Webpage）"),
        new(ZoteroItemType.JournalArticle, "期刊文章（JournalArticle）")
    ];
}

/// <summary>
/// 管理 Zotero 个人库的非敏感目标与 DPAPI API key；初始化从不回读凭据明文，
/// 本机保存也不会隐式发起第三方请求。
/// </summary>
public sealed class ZoteroSettingsViewModel
    : ObservableObject, IDisposable
{
    private readonly IZoteroExportTargetStore _targetStore;
    private readonly IEntryIntegrationCredentialStore _credentials;
    private readonly IEntryIntegrationHealthService _health;
    private readonly IReadOnlyList<ZoteroItemTypeChoice> _itemTypes =
        ZoteroItemTypeChoice.All;
    private string _userIdText = string.Empty;
    private ZoteroItemTypeChoice _selectedItemType =
        ZoteroItemTypeChoice.All[0];
    private bool _includeSummaryNote;
    private bool _uploadFirstImageAttachment;
    private string _credentialInput = string.Empty;
    private bool _hasCredential;
    private bool _isBusy;
    private string _status =
        "Zotero 仅支持个人库；目标设置保存在本机，API key 只进入 Windows DPAPI。";

    public ZoteroSettingsViewModel(
        IZoteroExportTargetStore targetStore,
        IEntryIntegrationCredentialStore credentials,
        IEntryIntegrationHealthService health)
    {
        _targetStore = targetStore
            ?? throw new ArgumentNullException(nameof(targetStore));
        _credentials = credentials
            ?? throw new ArgumentNullException(nameof(credentials));
        _health = health
            ?? throw new ArgumentNullException(nameof(health));
        SaveCommand = new(SaveAsync, CanOperate);
        DeleteCredentialCommand = new(
            DeleteCredentialAsync,
            () => CanOperate() && HasCredential);
        TestCommand = new(TestAsync, CanOperate);
    }

    public IReadOnlyList<ZoteroItemTypeChoice> ItemTypes =>
        _itemTypes;
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand DeleteCredentialCommand { get; }
    public AsyncRelayCommand TestCommand { get; }

    public string UserIdText
    {
        get => _userIdText;
        set => SetProperty(ref _userIdText, value ?? string.Empty);
    }

    public ZoteroItemTypeChoice SelectedItemType
    {
        get => _selectedItemType;
        set => SetProperty(
            ref _selectedItemType,
            value ?? ZoteroItemTypeChoice.All[0]);
    }

    public bool IncludeSummaryNote
    {
        get => _includeSummaryNote;
        set => SetProperty(ref _includeSummaryNote, value);
    }

    public bool UploadFirstImageAttachment
    {
        get => _uploadFirstImageAttachment;
        set => SetProperty(ref _uploadFirstImageAttachment, value);
    }

    public string CredentialInput
    {
        get => _credentialInput;
        set => SetProperty(
            ref _credentialInput,
            value ?? string.Empty);
    }

    public bool HasCredential
    {
        get => _hasCredential;
        private set
        {
            if (SetProperty(ref _hasCredential, value))
            {
                DeleteCredentialCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            SaveCommand.NotifyCanExecuteChanged();
            DeleteCredentialCommand.NotifyCanExecuteChanged();
            TestCommand.NotifyCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// 恢复非敏感目标并只检查凭据是否存在；API key 明文不会进入 ViewModel。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            ZoteroExportTarget? target =
                await _targetStore.GetAsync(cancellationToken);
            if (target is not null)
            {
                UserIdText = target.UserId.ToString(
                    CultureInfo.InvariantCulture);
                SelectedItemType = ItemTypes.Single(item =>
                    item.Value == target.ItemType);
                IncludeSummaryNote = target.IncludeSummaryNote;
                UploadFirstImageAttachment =
                    target.UploadFirstImageAttachment;
            }

            HasCredential = await _credentials.ExistsAsync(
                EntryIntegrationKind.Zotero,
                ZoteroExportTarget.DefaultTargetId,
                cancellationToken);
            Status = target is null
                ? "尚未保存 Zotero 个人库目标。"
                : "已加载 Zotero 个人库目标；API key 未回读。";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            HasCredential = false;
            Status = "Zotero 本机设置暂时无法读取。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            ZoteroExportTarget target = BuildCurrentTarget();
            // 保存与测试刻意分离：这里仅更新本机目标和 DPAPI，不调用 Zotero。
            await _targetStore.SaveAsync(target, cancellationToken);
            if (!string.IsNullOrWhiteSpace(CredentialInput))
            {
                await _credentials.SetAsync(
                    EntryIntegrationKind.Zotero,
                    ZoteroExportTarget.DefaultTargetId,
                    CredentialInput.Trim(),
                    cancellationToken);
            }
            HasCredential = await _credentials.ExistsAsync(
                EntryIntegrationKind.Zotero,
                ZoteroExportTarget.DefaultTargetId,
                cancellationToken);
            Status = HasCredential
                ? "Zotero 个人库目标已保存，API key 已由 Windows DPAPI 加密。"
                : "Zotero 个人库目标已保存；尚未填写 API key。";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            Status = "保存 Zotero 本机设置时无法访问安全存储。";
        }
        finally
        {
            // 不论校验或存储是否成功，保存动作都不在界面中保留 API key。
            CredentialInput = string.Empty;
            IsBusy = false;
        }
    }

    private async Task DeleteCredentialAsync(
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await _credentials.DeleteAsync(
                EntryIntegrationKind.Zotero,
                ZoteroExportTarget.DefaultTargetId,
                cancellationToken);
            CredentialInput = string.Empty;
            HasCredential = false;
            Status = "Zotero API key 已从本机 DPAPI 存储删除。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestAsync(CancellationToken cancellationToken)
    {
        ZoteroExportTarget target;
        try
        {
            target = BuildCurrentTarget();
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
            return;
        }

        IsBusy = true;
        try
        {
            // 健康探针必须与耐久导出使用同一已保存代际；未保存表单不能被测试成另一目标。
            ZoteroExportTarget? saved =
                await _targetStore.GetAsync(cancellationToken);
            if (saved is null
                || !string.Equals(
                    saved.CreateQueueTargetId(),
                    target.CreateQueueTargetId(),
                    StringComparison.Ordinal))
            {
                Status = "请先保存当前 Zotero 个人库设置，再测试连接。";
                return;
            }
            EntryIntegrationHealthResult result = await _health.CheckAsync(
                new EntryIntegrationTarget(
                    ZoteroExportTarget.DefaultTargetId,
                    EntryIntegrationKind.Zotero,
                    saved.ApiRoot),
                cancellationToken);
            Status = DescribeHealth(result);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            Status = "Zotero 本机设置暂时无法读取。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ZoteroExportTarget BuildCurrentTarget()
    {
        string value = UserIdText.Trim();
        if (!long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long userId)
            || userId <= 0)
        {
            throw new ArgumentException(
                "Zotero User ID 必须是正整数。");
        }

        var target = new ZoteroExportTarget(
            ZoteroExportTarget.DefaultTargetId,
            userId,
            SelectedItemType.Value,
            IncludeSummaryNote,
            UploadFirstImageAttachment);
        _ = target.ApiRoot;
        return target;
    }

    private static string DescribeHealth(
        EntryIntegrationHealthResult result) => result.Status switch
        {
            EntryIntegrationHealthStatus.Healthy =>
                "Zotero 个人库连接检查通过。",
            EntryIntegrationHealthStatus.PolicyDisabled =>
                "管理员尚未启用 Zotero 或官方主机不在共享策略中。",
            EntryIntegrationHealthStatus.BlockedEndpoint =>
                "Zotero 官方地址或解析结果被安全策略阻止。",
            EntryIntegrationHealthStatus.CredentialsMissing =>
                "请先保存 Zotero API key。",
            EntryIntegrationHealthStatus.AdapterUnavailable =>
                "Zotero 连接适配器尚未安装，未发起外部请求。",
            EntryIntegrationHealthStatus.Unauthorized =>
                "Zotero 拒绝了当前 API key。",
            EntryIntegrationHealthStatus.RateLimited =>
                $"Zotero 检查过于频繁，请在 {Math.Ceiling(result.RetryAfter?.TotalSeconds ?? 1)} 秒后重试。",
            EntryIntegrationHealthStatus.TimedOut =>
                "Zotero 连接检查超时。",
            _ => "Zotero 连接检查暂时不可用。"
        };

    private bool CanOperate() => !IsBusy;

    public void Dispose()
    {
        SaveCommand.Dispose();
        DeleteCredentialCommand.Dispose();
        TestCommand.Dispose();
    }
}
