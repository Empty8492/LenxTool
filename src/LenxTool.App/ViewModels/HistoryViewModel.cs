using System.Collections.ObjectModel;
using System.IO;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Media;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly IMediaJobRepository _jobs;
    private readonly IDatabaseMaintenanceService _database;
    private readonly IDesktopFileDialogService _dialogs;
    private readonly INewsRepository _news;
    private readonly ISubtitleRepository _subtitles;
    private readonly ISubtitleExportService _subtitleExporter;
    private readonly IEntryStateRepository _entryStates;
    private readonly IFavoriteRepository _favorites;
    private readonly IFeedCatalogRepository? _searchCatalogRepository;
    private readonly IAppNavigationService? _navigationService;
    private MediaJob? _selectedJob;
    private ContentSearchResult? _selectedSearchResult;
    private string _searchQuery = string.Empty;
    private string _searchStatus = "输入关键词，搜索已缓存的 Feed、早报、热点、AI 报告、字幕、标签和收藏。";
    private string _status = "任务、错误和输出文件均保存在本机。";
    private Task _selectedJobLoad = Task.CompletedTask;
    private int _selectedJobLoadVersion;
    private string _providerSummary = "暂无模型调用";
    private string _usageSummary = "0 次请求 · 0 tokens";
    private string _errorSummary = "暂无错误";
    private SubtitleExportOption _selectedExportOption;

    public HistoryViewModel(
        IMediaJobRepository jobs,
        IDatabaseMaintenanceService database,
        IDesktopFileDialogService dialogs,
        INewsRepository news,
        ISubtitleRepository subtitles,
        ISubtitleExportService subtitleExporter,
        IEntryStateRepository entryStates,
        IFavoriteRepository favorites,
        IFeedCatalogRepository? searchCatalogRepository = null,
        IAppNavigationService? navigationService = null)
        : base("历史与数据", "搜索任务、查看输出，并管理 SQLite 数据库备份")
    {
        _jobs = jobs;
        _database = database;
        _dialogs = dialogs;
        _news = news;
        _subtitles = subtitles;
        _subtitleExporter = subtitleExporter;
        _entryStates = entryStates;
        _favorites = favorites;
        _searchCatalogRepository = searchCatalogRepository;
        _navigationService = navigationService;
        _selectedExportOption = ExportOptions[0];
        RefreshCommand = new(LoadAsync);
        BackupCommand = new(BackupAsync);
        RestoreCommand = new(RestoreAsync);
        OpenOutputCommand = new(OpenOutput, () => SelectedJob?.OutputPath is not null);
        ConfigureSearch();
        SearchCommand = new(SearchAsync, CanStartSearch);
        OpenSearchResultCommand = new(
            OpenSearchResultAsync,
            CanOpenSearchResult);
        ExportSubtitleCommand = new(ExportSubtitleAsync, CanExportSubtitle);
        ConfigureSelectedSearchPrivateState();
    }

    public ObservableCollection<MediaJob> Jobs { get; } = [];
    public ObservableCollection<ContentSearchResult> SearchResults { get; } = [];
    public ObservableCollection<SubtitleSegment> SubtitleSegments { get; } = [];
    public IReadOnlyList<SubtitleExportOption> ExportOptions { get; } =
    [
        new(SubtitleExportMode.OriginalSrt, "原文 SRT"),
        new(SubtitleExportMode.TranslatedSrt, "译文 SRT"),
        new(SubtitleExportMode.BilingualSrt, "双语 SRT"),
        new(SubtitleExportMode.PlainText, "纯文本 TXT")
    ];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand BackupCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand OpenSearchResultCommand { get; }
    public AsyncRelayCommand ExportSubtitleCommand { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public MediaJob? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetProperty(ref _selectedJob, value)) return;
            OpenOutputCommand.NotifyCanExecuteChanged();
            int loadVersion = ++_selectedJobLoadVersion;
            _selectedJobLoad = LoadSelectedJobAsync(value, loadVersion);
            OnPropertyChanged(nameof(SelectedJobLoad));
            OnPropertyChanged(nameof(ProviderSummary));
            OnPropertyChanged(nameof(UsageSummary));
            OnPropertyChanged(nameof(ErrorSummary));
            ExportSubtitleCommand.NotifyCanExecuteChanged();
        }
    }

    public Task SelectedJobLoad => _selectedJobLoad;

    public string ProviderSummary
    {
        get => _providerSummary;
        private set => SetProperty(ref _providerSummary, value);
    }

    public string UsageSummary
    {
        get => _usageSummary;
        private set => SetProperty(ref _usageSummary, value);
    }

    public string ErrorSummary
    {
        get => _errorSummary;
        private set => SetProperty(ref _errorSummary, value);
    }

    public SubtitleExportOption SelectedExportOption
    {
        get => _selectedExportOption;
        set
        {
            if (SetProperty(ref _selectedExportOption, value))
            {
                ExportSubtitleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? string.Empty))
            {
                InvalidateSearchPaging();
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
                OnPropertyChanged(nameof(OpenSearchResultLabel));
                OnSelectedSearchResultChanged(value);
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            LoadAsync(cancellationToken),
            InitializeSearchFiltersAsync(cancellationToken));
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaJob> recent = await _jobs.GetRecentAsync(200, cancellationToken);
        SelectedJob = null;
        Jobs.Clear();
        foreach (MediaJob job in recent) Jobs.Add(job);
        SelectedJob = Jobs.FirstOrDefault();
        await SelectedJobLoad;
        Status = $"共 {Jobs.Count} 条媒体任务记录。";
    }

    private async Task LoadSelectedJobAsync(MediaJob? job, int loadVersion)
    {
        if (job is null)
        {
            if (loadVersion != _selectedJobLoadVersion) return;
            SubtitleSegments.Clear();
            ProviderSummary = "暂无模型调用";
            UsageSummary = "0 次请求 · 0 tokens";
            ErrorSummary = "暂无错误";
            return;
        }

        try
        {
            IReadOnlyList<SubtitleSegment> segments = await _subtitles.GetByMediaJobIdAsync(
                job.Id,
                CancellationToken.None);
            if (loadVersion != _selectedJobLoadVersion) return;
            SubtitleSegments.Clear();
            foreach (SubtitleSegment segment in segments) SubtitleSegments.Add(segment);
            string provider = job.TranslationProvider ?? job.Engine.ToString();
            ProviderSummary = string.IsNullOrWhiteSpace(job.Model)
                ? provider
                : $"{provider} · {job.Model}";
            UsageSummary = $"{job.AiRequestCount} 次请求 · {job.TranslationTotalTokens} tokens（输入 {job.TranslationPromptTokens} / 输出 {job.TranslationCompletionTokens}）";
            ErrorSummary = job.Error?.UserMessage ?? "暂无错误";
        }
        catch (Exception)
        {
            if (loadVersion != _selectedJobLoadVersion) return;
            SubtitleSegments.Clear();
            ProviderSummary = "本地详情读取失败";
            UsageSummary = "用量暂不可用";
            ErrorSummary = "无法读取该任务的字幕详情。";
        }
        finally
        {
            if (loadVersion == _selectedJobLoadVersion)
            {
                ExportSubtitleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool CanExportSubtitle() =>
        SelectedJob is not null &&
        SubtitleSegments.Count > 0 &&
        (SelectedExportOption.Mode == SubtitleExportMode.OriginalSrt ||
         SubtitleSegments.All(segment => !string.IsNullOrWhiteSpace(segment.TranslatedText)));

    private async Task ExportSubtitleAsync(CancellationToken cancellationToken)
    {
        await SelectedJobLoad;
        if (SelectedJob is not { } job || SubtitleSegments.Count == 0) return;
        string path = await _subtitleExporter.ExportAsync(
            job,
            SubtitleSegments.ToArray(),
            SelectedExportOption.Mode,
            cancellationToken);
        MediaJob updated = job with { OutputPath = path, UpdatedAt = DateTimeOffset.UtcNow };
        await _jobs.UpsertAsync(updated, cancellationToken);
        int index = Jobs.IndexOf(job);
        if (index >= 0) Jobs[index] = updated;
        _selectedJob = updated;
        OnPropertyChanged(nameof(SelectedJob));
        OpenOutputCommand.NotifyCanExecuteChanged();
        Status = $"字幕已重新导出：{Path.GetFileName(path)}";
    }

    private async Task BackupAsync(CancellationToken cancellationToken)
    {
        string path = await _database.BackupAsync(null, cancellationToken);
        Status = $"数据库已备份：{path}";
        _dialogs.OpenFolder(path);
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        await SearchPageAsync(reset: true, cancellationToken);
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


    private bool CanOpenSearchResult() =>
        SelectedSearchResult?.Type switch
        {
            ContentSearchResultType.FeedEntry =>
                _navigationService is not null
                || CanOpenUri(SelectedSearchResult.Url),
            ContentSearchResultType.Subtitle => true,
            _ => CanOpenUri(SelectedSearchResult?.Url)
        };

    private async Task OpenSearchResultAsync(
        CancellationToken cancellationToken)
    {
        ContentSearchResult? result = SelectedSearchResult;
        if (result is null)
        {
            return;
        }
        if (result.Type == ContentSearchResultType.FeedEntry
            && _navigationService is not null)
        {
            await _navigationService.NavigateAsync(
                new("news", "feed_entry", result.EntityId),
                cancellationToken);
            return;
        }
        if (result.Type == ContentSearchResultType.Subtitle)
        {
            MediaJob? job = await _jobs.GetByIdAsync(
                result.EntityId,
                cancellationToken);
            if (job is null)
            {
                SearchStatus = "对应的字幕任务已被清理。";
                return;
            }
            MediaJob? existing = Jobs.FirstOrDefault(
                item => string.Equals(
                    item.Id,
                    job.Id,
                    StringComparison.Ordinal));
            if (existing is null)
            {
                Jobs.Insert(0, job);
            }
            SelectedJob = existing ?? job;
            SelectedHistoryTabIndex = 1;
            Status = $"已打开字幕任务：{Path.GetFileName(job.InputPath)}";
            return;
        }
        if (CanOpenUri(result.Url))
        {
            _dialogs.OpenUri(result.Url!);
        }
    }

    private static bool CanOpenUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https";

    private static string NormalizeSearchIdentity(ContentSearchResult result)
    {
        if (Uri.TryCreate(result.Url, UriKind.Absolute, out Uri? uri))
        {
            return uri.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.Path | UriComponents.Query,
                UriFormat.UriEscaped).TrimEnd('/');
        }

        return $"{result.Title}\u001f{result.Source}";
    }

}
