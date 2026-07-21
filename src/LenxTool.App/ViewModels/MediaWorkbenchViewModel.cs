using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Media;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.App.ViewModels;

public sealed class MediaWorkbenchViewModel : PageViewModel
{
    private readonly IMediaJobRepository _jobs;
    private readonly ISubtitleRepository _subtitles;
    private readonly ITranscriptionService _groq;
    private readonly ILocalTranscriptionService _local;
    private readonly IMediaAudioService _audio;
    private readonly ILocalModelService _models;
    private readonly IDesktopFileDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly List<MediaJob> _pendingJobs = [];
    private string _inputSummary = "尚未选择文件";
    private string _status = "支持批量导入；任务按顺序执行，可随时取消。";
    private string _selectedEngine = "Groq Whisper";
    private LocalModelInfo? _selectedModel;
    private double _progress;
    private string? _lastOutputPath;
    private AppError? _lastError;

    public MediaWorkbenchViewModel(
        IMediaJobRepository jobs,
        ISubtitleRepository subtitles,
        ITranscriptionService groq,
        ILocalTranscriptionService local,
        IMediaAudioService audio,
        ILocalModelService models,
        IDesktopFileDialogService dialogs,
        AppPaths paths) : base("媒体工作台", "批量转写音视频，使用云端 Groq 或完全离线的本地 Whisper")
    {
        _jobs = jobs;
        _subtitles = subtitles;
        _groq = groq;
        _local = local;
        _audio = audio;
        _models = models;
        _dialogs = dialogs;
        _paths = paths;
        ImportModelCommand = new(ImportModelAsync);
        StartCommand = new(ProcessQueueAsync, () => _pendingJobs.Count > 0);
        BrowseCommand = new(BrowseAsync, () => !StartCommand.IsRunning);
        CancelCommand = new(() => StartCommand.Cancel(), () => StartCommand.IsRunning);
        OpenOutputCommand = new(OpenOutput, () => !string.IsNullOrWhiteSpace(LastOutputPath));
        RetryCommand = new(RetryAsync, job => job?.Status is MediaJobStatus.Failed or MediaJobStatus.Cancelled);
        StartCommand.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(AsyncRelayCommand.IsRunning)) return;
            BrowseCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        };
    }

    public IReadOnlyList<string> Engines { get; } = ["Groq Whisper", "本地 Whisper"];
    public ObservableCollection<LocalModelInfo> LocalModels { get; } = [];
    public ObservableCollection<MediaJob> RecentJobs { get; } = [];
    public AsyncRelayCommand BrowseCommand { get; }
    public AsyncRelayCommand ImportModelCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public AsyncRelayCommand<MediaJob> RetryCommand { get; }

    public string InputSummary
    {
        get => _inputSummary;
        private set => SetProperty(ref _inputSummary, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SelectedEngine
    {
        get => _selectedEngine;
        set => SetProperty(ref _selectedEngine, value);
    }

    public LocalModelInfo? SelectedModel
    {
        get => _selectedModel;
        set => SetProperty(ref _selectedModel, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string? LastOutputPath
    {
        get => _lastOutputPath;
        private set
        {
            if (SetProperty(ref _lastOutputPath, value)) OpenOutputCommand.NotifyCanExecuteChanged();
        }
    }

    public AppError? LastError
    {
        get => _lastError;
        private set
        {
            if (SetProperty(ref _lastError, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => LastError is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaJob> recovered = await _jobs.RecoverInterruptedAsync(cancellationToken);
        IReadOnlyList<MediaJob> queued = await _jobs.GetQueuedAsync(cancellationToken);
        IReadOnlyList<LocalModelInfo> models = await _models.ListAsync(cancellationToken);
        RecentJobs.Clear();
        foreach (MediaJob job in recovered) RecentJobs.Add(job);
        _pendingJobs.Clear();
        _pendingJobs.AddRange(queued);
        UpdateQueueSummary();
        StartCommand.NotifyCanExecuteChanged();
        LocalModels.Clear();
        foreach (LocalModelInfo model in models) LocalModels.Add(model);
        SelectedModel ??= LocalModels.FirstOrDefault();
    }

    private async Task BrowseAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> files = _dialogs.PickMediaFiles();
        if (files.Count == 0) return;

        string[] subtitleFiles = files.Where(IsSubtitleFile).ToArray();
        string[] mediaFiles = files.Where(path => !IsSubtitleFile(path)).ToArray();
        bool useLocal = SelectedEngine == Engines[1];
        if (mediaFiles.Length > 0 && useLocal && SelectedModel is null)
        {
            LastError = new(
                AppErrorCode.InvalidRequest, "尚未导入本地模型", "本地 Whisper 需要 ggml-*.bin 模型。",
                "点击“导入模型”后选择现有模型，或切换到 Groq Whisper。");
            Status = LastError.UserMessage;
            return;
        }

        LastError = null;
        var subtitleImports = new List<(string Path, IReadOnlyList<SubtitleSegment> Segments)>();
        try
        {
            foreach (string inputPath in subtitleFiles)
            {
                string content = await File.ReadAllTextAsync(inputPath, cancellationToken);
                IReadOnlyList<SubtitleSegment> segments = SrtCodec.Parse(content);
                if (segments.Count == 0)
                {
                    throw new FormatException("SRT 文件未包含任何有效字幕片段。");
                }
                subtitleImports.Add((inputPath, segments));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = CreateSubtitleImportError(exception);
            Status = LastError.UserMessage;
            return;
        }

        int importedSegmentCount = 0;
        try
        {
            foreach ((string inputPath, IReadOnlyList<SubtitleSegment> segments) in subtitleImports)
            {
                MediaJob job = CreateImportedSubtitleJob(inputPath);
                await _subtitles.CreateMediaJobWithSegmentsAsync(job, segments, cancellationToken);
                importedSegmentCount += segments.Count;
                AddOrReplace(job);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = CreateSubtitleImportError(exception);
            Status = LastError.UserMessage;
            return;
        }

        foreach (string inputPath in mediaFiles)
        {
            MediaJob job = CreateQueuedJob(inputPath, useLocal);
            await _jobs.UpsertAsync(job, cancellationToken);
            _pendingJobs.Add(job);
            AddOrReplace(job);
        }
        UpdateQueueSummary();
        Status = (subtitleImports.Count, mediaFiles.Length) switch
        {
            (> 0, 0) => $"已导入 {subtitleImports.Count} 个 SRT，共 {importedSegmentCount} 个片段；关闭后仍可恢复。",
            (0, > 0) => $"已持久化 {mediaFiles.Length} 个待处理任务，可安全关闭后继续。",
            _ => $"已导入 {subtitleImports.Count} 个 SRT，并持久化 {mediaFiles.Length} 个媒体任务。"
        };
        StartCommand.NotifyCanExecuteChanged();
    }

    private async Task ImportModelAsync(CancellationToken cancellationToken)
    {
        string? source = _dialogs.PickWhisperModel();
        if (source is null) return;
        Status = "正在校验并导入本地模型…";
        LocalModelInfo model = await _models.ImportAsync(source, cancellationToken);
        LocalModels.Remove(LocalModels.FirstOrDefault(item => item.Path == model.Path)!);
        LocalModels.Insert(0, model);
        SelectedModel = model;
        SelectedEngine = Engines[1];
        Status = $"模型已导入：{model.Name}";
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        LastError = null;
        _paths.EnsureCreated();
        int succeeded = 0;
        int failed = 0;
        int total = _pendingJobs.Count;
        foreach (MediaJob queuedJob in _pendingJobs.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaJobStatus result;
            try
            {
                result = await ProcessOneAsync(queuedJob, succeeded + failed, total, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _pendingJobs.RemoveAll(job => job.Id == queuedJob.Id);
                UpdateQueueSummary();
                throw;
            }
            _pendingJobs.RemoveAll(job => job.Id == queuedJob.Id);
            if (result == MediaJobStatus.Completed) succeeded++;
            else if (result == MediaJobStatus.Failed) failed++;
            Progress = (succeeded + failed) * 100d / total;
            UpdateQueueSummary();
        }
        StartCommand.NotifyCanExecuteChanged();
        Status = $"处理结束：成功 {succeeded} 个，失败 {failed} 个。";
    }

    private async Task<MediaJobStatus> ProcessOneAsync(
        MediaJob queuedJob,
        int completedBeforeCurrent,
        int queueTotal,
        CancellationToken cancellationToken)
    {
        string inputPath = queuedJob.InputPath;
        bool useLocal = queuedJob.Engine == TranscriptionEngine.LocalWhisper;
        LocalModelInfo? localModel = useLocal
            ? LocalModels.FirstOrDefault(model => model.Name == queuedJob.Model || model.Path == queuedJob.Model)
            : null;
        var job = queuedJob with { Status = MediaJobStatus.Running, Progress = 0, Error = null, UpdatedAt = DateTimeOffset.UtcNow };
        await _jobs.UpsertAsync(job, cancellationToken);
        AddOrReplace(job);
        PreparedAudio? prepared = null;
        Task progressWrites = Task.CompletedTask;
        object progressGate = new();
        try
        {
            if (useLocal && localModel is null)
            {
                throw new AppException(new(
                    AppErrorCode.InvalidRequest, "本地模型不可用", "队列任务所需的本地 Whisper 模型已被移除。",
                    "重新导入对应模型后重试，或改用 Groq Whisper 新建任务。"));
            }
            Status = $"正在准备：{Path.GetFileName(inputPath)}";
            prepared = await _audio.PrepareAsync(inputPath, cancellationToken);
            var progress = new InlineProgress<double>(value =>
            {
                double normalized = Math.Clamp(value, 0, 100);
                MediaJob snapshot;
                lock (progressGate)
                {
                    Progress = (completedBeforeCurrent + normalized / 100d) * 100d / queueTotal;
                    Status = $"正在识别：{Path.GetFileName(inputPath)} · {normalized:0}%";
                    job = job with { Progress = normalized, UpdatedAt = DateTimeOffset.UtcNow };
                    snapshot = job;
                    progressWrites = PersistAfterAsync(progressWrites, snapshot);
                }
            });
            ITranscriptionService service = useLocal ? _local : _groq;
            IReadOnlyList<SubtitleSegment> segments = await service.TranscribeAsync(
                prepared.Path, useLocal ? localModel!.Path : "whisper-large-v3", progress, cancellationToken);
            Task pendingWrites;
            lock (progressGate) pendingWrites = progressWrites;
            await pendingWrites;
            string outputPath = UniqueOutputPath(inputPath);
            await File.WriteAllTextAsync(outputPath, SrtCodec.Export(segments, SubtitleExportMode.OriginalSrt),
                new UTF8Encoding(false), cancellationToken);
            job = job with { OutputPath = outputPath, Status = MediaJobStatus.Completed, Progress = 100, UpdatedAt = DateTimeOffset.UtcNow };
            LastOutputPath = outputPath;
        }
        catch (OperationCanceledException)
        {
            job = job with { Status = MediaJobStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow,
                Error = new(AppErrorCode.OperationCancelled, "任务已取消", "媒体处理已由用户取消。", "可从历史记录重新执行。", IsRetryable: true) };
            throw;
        }
        catch (Exception exception)
        {
            AppError error = exception is AppException appException ? appException.Error : new(
                AppErrorCode.Unknown, "媒体任务失败", "无法完成当前媒体任务。", "检查文件格式后重试，或切换识别方式。",
                exception.Message, IsRetryable: true);
            LastError = error;
            Status = error.UserMessage;
            job = job with { Status = MediaJobStatus.Failed, Error = error, UpdatedAt = DateTimeOffset.UtcNow };
        }
        finally
        {
            if (prepared?.IsTemporary == true)
            {
                try { File.Delete(prepared.Path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await _jobs.UpsertAsync(job, CancellationToken.None);
            AddOrReplace(job);
        }
        return job.Status;
    }

    private async Task RetryAsync(MediaJob? failedJob, CancellationToken cancellationToken)
    {
        if (failedJob?.Status is not (MediaJobStatus.Failed or MediaJobStatus.Cancelled)) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MediaJob retry = failedJob with
        {
            Id = Guid.NewGuid().ToString("N"),
            OutputPath = null,
            Status = MediaJobStatus.Queued,
            Progress = 0,
            Error = null,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _jobs.UpsertAsync(retry, cancellationToken);
        _pendingJobs.Add(retry);
        AddOrReplace(retry);
        UpdateQueueSummary();
        Status = "失败任务已重新加入持久化队列。";
        StartCommand.NotifyCanExecuteChanged();
    }

    private MediaJob CreateQueuedJob(string inputPath, bool useLocal)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(
            Guid.NewGuid().ToString("N"), "Transcription", inputPath, null, MediaJobStatus.Queued, 0,
            useLocal ? TranscriptionEngine.LocalWhisper : TranscriptionEngine.Groq,
            useLocal ? SelectedModel!.Name : "whisper-large-v3", 0, 0, null, now, now);
    }

    private static MediaJob CreateImportedSubtitleJob(string inputPath)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(
            Guid.NewGuid().ToString("N"),
            "SubtitleImport",
            inputPath,
            null,
            MediaJobStatus.Completed,
            100,
            TranscriptionEngine.ImportedSrt,
            null,
            0,
            0,
            null,
            now,
            now);
    }

    private static bool IsSubtitleFile(string path) =>
        string.Equals(Path.GetExtension(path), ".srt", StringComparison.OrdinalIgnoreCase);

    private static AppError CreateSubtitleImportError(Exception exception) => exception switch
    {
        FormatException => new(
            AppErrorCode.InvalidRequest,
            "SRT 格式无效",
            exception.Message,
            "请修正提示行的序号、时间轴或正文后重新导入。"),
        FileNotFoundException => new(
            AppErrorCode.FileNotFound,
            "SRT 文件不存在",
            "所选 SRT 文件已被移动或删除。",
            "重新选择现有的 SRT 文件后再试。"),
        UnauthorizedAccessException => new(
            AppErrorCode.FileAccessDenied,
            "无法读取 SRT 文件",
            "当前 Windows 用户没有读取所选文件的权限。",
            "复制到当前用户可读目录后重新导入。"),
        IOException => new(
            AppErrorCode.FileAccessDenied,
            "无法读取 SRT 文件",
            "读取或保存字幕时发生文件系统错误。",
            "确认文件未被占用且磁盘可写，然后重试。",
            IsRetryable: true),
        _ => new(
            AppErrorCode.Unknown,
            "SRT 导入失败",
            "字幕未能完整写入本地数据库。",
            "请重试；若仍失败，请先备份数据库并检查磁盘空间。",
            IsRetryable: true)
    };

    private async Task PersistAfterAsync(Task previousWrite, MediaJob job)
    {
        await previousWrite.ConfigureAwait(false);
        await _jobs.UpsertAsync(job, CancellationToken.None).ConfigureAwait(false);
    }

    private void UpdateQueueSummary()
    {
        InputSummary = _pendingJobs.Count switch
        {
            0 => "当前没有待处理任务",
            1 => _pendingJobs[0].InputPath,
            _ => $"待处理 {_pendingJobs.Count} 个文件 · {Path.GetFileName(_pendingJobs[0].InputPath)} 等"
        };
    }

    private string UniqueOutputPath(string inputPath)
    {
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string path = Path.Combine(_paths.OutputDirectory, name + ".srt");
        return File.Exists(path) ? Path.Combine(_paths.OutputDirectory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.srt") : path;
    }

    private void AddOrReplace(MediaJob job)
    {
        MediaJob? existing = RecentJobs.FirstOrDefault(item => item.Id == job.Id);
        if (existing is not null) RecentJobs.Remove(existing);
        RecentJobs.Insert(0, job);
        while (RecentJobs.Count > 50) RecentJobs.RemoveAt(RecentJobs.Count - 1);
    }

    private void OpenOutput()
    {
        if (LastOutputPath is not null) _dialogs.OpenFolder(LastOutputPath);
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
