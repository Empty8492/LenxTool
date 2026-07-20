using System.Collections.ObjectModel;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed class HistoryViewModel : PageViewModel
{
    private readonly IMediaJobRepository _jobs;
    private readonly IDatabaseMaintenanceService _database;
    private readonly IDesktopFileDialogService _dialogs;
    private readonly INewsRepository _news;
    private MediaJob? _selectedJob;
    private ContentSearchResult? _selectedSearchResult;
    private string _searchQuery = string.Empty;
    private string _searchStatus = "输入关键词，搜索已缓存的早报、热点和 AI 报告。";
    private string _status = "任务、错误和输出文件均保存在本机。";

    public HistoryViewModel(
        IMediaJobRepository jobs,
        IDatabaseMaintenanceService database,
        IDesktopFileDialogService dialogs,
        INewsRepository news) : base("历史与数据", "搜索任务、查看输出，并管理 SQLite 数据库备份")
    {
        _jobs = jobs;
        _database = database;
        _dialogs = dialogs;
        _news = news;
        RefreshCommand = new(LoadAsync);
        BackupCommand = new(BackupAsync);
        RestoreCommand = new(RestoreAsync);
        OpenOutputCommand = new(OpenOutput, () => SelectedJob?.OutputPath is not null);
        SearchCommand = new(SearchAsync, () => !string.IsNullOrWhiteSpace(SearchQuery));
        OpenSearchResultCommand = new(OpenSearchResult, () => SelectedSearchResult?.Url is not null);
    }

    public ObservableCollection<MediaJob> Jobs { get; } = [];
    public ObservableCollection<ContentSearchResult> SearchResults { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand BackupCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand OpenSearchResultCommand { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public MediaJob? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (SetProperty(ref _selectedJob, value)) OpenOutputCommand.NotifyCanExecuteChanged();
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? string.Empty))
            {
                SearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public ContentSearchResult? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (SetProperty(ref _selectedSearchResult, value))
            {
                OpenSearchResultCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaJob> recent = await _jobs.GetRecentAsync(200, cancellationToken);
        Jobs.Clear();
        foreach (MediaJob job in recent) Jobs.Add(job);
        Status = $"共 {Jobs.Count} 条媒体任务记录。";
    }

    private async Task BackupAsync(CancellationToken cancellationToken)
    {
        string path = await _database.BackupAsync(null, cancellationToken);
        Status = $"数据库已备份：{path}";
        _dialogs.OpenFolder(path);
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentSearchResult> results = await _news.SearchContentAsync(
            SearchQuery.Trim(),
            100,
            cancellationToken);
        SearchResults.Clear();
        foreach (ContentSearchResult result in results) SearchResults.Add(result);
        SelectedSearchResult = SearchResults.FirstOrDefault();
        SearchStatus = SearchResults.Count == 0
            ? "没有找到相关内容；请尝试更短或不同的关键词。"
            : $"找到 {SearchResults.Count} 条相关内容。";
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        string? path = _dialogs.PickDatabaseBackup();
        if (path is null) return;
        await _database.RestoreAsync(path, cancellationToken);
        Status = "数据库已恢复；恢复前的当前数据库也已自动备份。";
        await LoadAsync(cancellationToken);
    }

    private void OpenOutput()
    {
        if (SelectedJob?.OutputPath is { } path) _dialogs.OpenFolder(path);
    }


    private void OpenSearchResult()
    {
        if (SelectedSearchResult?.Url is { } uri) _dialogs.OpenUri(uri);
    }
}
