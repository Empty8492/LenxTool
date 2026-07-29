using System.IO;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.App.ViewModels;

/// <summary>
/// 管理本机 Obsidian Vault 导出目标；保存后后台导出会读取最新配置，无需重启应用。
/// </summary>
public sealed class ObsidianSettingsViewModel : ObservableObject
{
    private readonly IObsidianExportTargetStore _targetStore;
    private readonly IDesktopFileDialogService _fileDialogs;
    private string _vaultRootPath = string.Empty;
    private string _relativeDirectory = string.Empty;
    private string _tagsText = string.Empty;
    private string _templateMarkdown = string.Empty;
    private bool _includeSourceLink = true;
    private string _status =
        "尚未配置 Obsidian Vault；只有显式导出操作才会写入 Markdown 文件。";
    private bool _isBusy;

    public ObsidianSettingsViewModel(
        IObsidianExportTargetStore targetStore,
        IDesktopFileDialogService fileDialogs)
    {
        _targetStore = targetStore;
        _fileDialogs = fileDialogs;
        PickVaultFolderCommand = new(PickVaultFolder);
        SaveCommand = new(SaveAsync);
    }

    public RelayCommand PickVaultFolderCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    public string VaultRootPath
    {
        get => _vaultRootPath;
        set => SetProperty(ref _vaultRootPath, value ?? string.Empty);
    }

    public string RelativeDirectory
    {
        get => _relativeDirectory;
        set => SetProperty(ref _relativeDirectory, value ?? string.Empty);
    }

    public string TagsText
    {
        get => _tagsText;
        set => SetProperty(ref _tagsText, value ?? string.Empty);
    }

    public string TemplateMarkdown
    {
        get => _templateMarkdown;
        set => SetProperty(ref _templateMarkdown, value ?? string.Empty);
    }

    public bool IncludeSourceLink
    {
        get => _includeSourceLink;
        set => SetProperty(ref _includeSourceLink, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            ObsidianExportTarget? target =
                await _targetStore.GetAsync(cancellationToken);
            if (target is null)
            {
                Status =
                    "尚未配置 Obsidian Vault；只有显式导出操作才会写入 Markdown 文件。";
                return;
            }

            VaultRootPath = target.VaultRootPath;
            RelativeDirectory = target.RelativeDirectory;
            TagsText = string.Join(", ", target.Tags);
            TemplateMarkdown = target.TemplateMarkdown ?? string.Empty;
            IncludeSourceLink = target.IncludeSourceLink;
            Status = "已加载 Obsidian 导出设置。";
        }
        catch (UnauthorizedAccessException)
        {
            Status = "当前用户无权读取该 Vault 设置。";
        }
        catch (IOException)
        {
            Status = "Obsidian 导出设置暂时无法读取。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PickVaultFolder()
    {
        string? selected = _fileDialogs.PickFolder();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            VaultRootPath = selected;
            Status = "已选择 Vault 根目录；保存后才会应用该配置。";
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var target = new ObsidianExportTarget(
                ObsidianEntryExporter.TargetId,
                VaultRootPath.Trim(),
                RelativeDirectory.Trim(),
                NormalizeOptionalTemplate(TemplateMarkdown),
                ParseTags(TagsText),
                IncludeSourceLink);
            await _targetStore.SaveAsync(target, cancellationToken);
            Status = "设置已保存，后续导出立即使用新配置。";
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
        }
        catch (InvalidOperationException)
        {
            Status = "Vault 路径包含不安全的重解析点。";
        }
        catch (UnauthorizedAccessException)
        {
            Status = "当前用户无权访问该 Vault。";
        }
        catch (IOException)
        {
            Status = "保存 Obsidian 设置时无法访问本地存储。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? NormalizeOptionalTemplate(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string[] ParseTags(string value) =>
        value.Split(
                [',', '，', ';', '；', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
