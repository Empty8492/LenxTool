using System.Text;
using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.App.Tests.ViewModels;

public sealed class MediaWorkbenchViewModelTests
{
    [Fact]
    public async Task QueuedInboxJobAppearsImmediatelyAndDuplicateIsIgnored()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new FakeRepository();
            var transcription = new FakeTranscription();
            var inbox = new MediaJobInbox();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(), new AppPaths(root), NoopTranslator, CreateExporter(root), inbox);
            await viewModel.InitializeAsync(CancellationToken.None);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var queued = new MediaJob(
                "feed-job-1",
                "FeedTranscription",
                Path.Combine(root, "feed.mp3"),
                null,
                MediaJobStatus.Queued,
                0,
                TranscriptionEngine.Groq,
                "whisper-large-v3",
                0,
                0,
                null,
                now,
                now);

            inbox.PublishQueued(queued);
            inbox.PublishQueued(queued);

            Assert.Equal(queued, Assert.Single(viewModel.RecentJobs));
            Assert.Equal(queued.InputPath, viewModel.InputSummary);
            Assert.True(viewModel.StartCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrowsePersistsSelectedFileAsQueuedBeforeProcessingStarts()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "queued.wav");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        try
        {
            var repository = new FakeRepository();
            var transcription = new FakeTranscription();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root), NoopTranslator, CreateExporter(root));

            await viewModel.InitializeAsync(CancellationToken.None);
            await viewModel.BrowseCommand.ExecuteAsync();

            MediaJob queued = Assert.Single(repository.Jobs.Values);
            Assert.Equal(MediaJobStatus.Queued, queued.Status);
            Assert.Equal(input, queued.InputPath);
            Assert.Equal(0, queued.Progress);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedJobIsCountedAsFailedInsteadOfCompleted()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "broken.wav");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        try
        {
            var repository = new FakeRepository();
            var transcription = new ThrowingTranscription();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root), NoopTranslator, CreateExporter(root));

            await viewModel.InitializeAsync(CancellationToken.None);
            await viewModel.BrowseCommand.ExecuteAsync();
            await viewModel.StartCommand.ExecuteAsync();

            Assert.Equal(MediaJobStatus.Failed, Assert.Single(repository.Jobs.Values).Status);
            Assert.Equal("处理结束：成功 0 个，失败 1 个。", viewModel.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeRestoresPersistedQueuedJobAndCanProcessIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "restored.wav");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var queued = new MediaJob("queued-1", "Transcription", input, null, MediaJobStatus.Queued, 0,
                TranscriptionEngine.Groq, "whisper-large-v3", 0, 0, null, now, now);
            var repository = new FakeRepository(queued);
            var transcription = new FakeTranscription();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root), NoopTranslator, CreateExporter(root));

            await viewModel.InitializeAsync(CancellationToken.None);

            Assert.True(viewModel.StartCommand.CanExecute(null));
            await viewModel.StartCommand.ExecuteAsync();
            Assert.Equal(MediaJobStatus.Completed, repository.Jobs[queued.Id].Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProgressReportsArePersistedBeforeCompletedState()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "progress.wav");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        try
        {
            var repository = new FakeRepository();
            var transcription = new FakeTranscription();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root), NoopTranslator, CreateExporter(root));

            await viewModel.InitializeAsync(CancellationToken.None);
            await viewModel.BrowseCommand.ExecuteAsync();
            await viewModel.StartCommand.ExecuteAsync();

            Assert.Contains(repository.Snapshots,
                job => job.Status == MediaJobStatus.Running && job.Progress == 37);
            Assert.Equal(MediaJobStatus.Completed, repository.Snapshots[^1].Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RetryCreatesFreshPersistedQueuedJobWithoutOldError()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "retry.wav");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        try
        {
            var repository = new FakeRepository();
            var transcription = new ThrowingTranscription();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root), NoopTranslator, CreateExporter(root));

            await viewModel.InitializeAsync(CancellationToken.None);
            await viewModel.BrowseCommand.ExecuteAsync();
            await viewModel.StartCommand.ExecuteAsync();
            MediaJob failed = Assert.Single(repository.Jobs.Values);

            await viewModel.RetryCommand.ExecuteAsync(failed);

            MediaJob retry = Assert.Single(repository.Jobs.Values, job => job.Status == MediaJobStatus.Queued);
            Assert.NotEqual(failed.Id, retry.Id);
            Assert.Null(retry.Error);
            Assert.Equal(0, retry.Progress);
            Assert.True(viewModel.StartCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BatchProgressDoesNotMoveBackwardBetweenJobs()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string first = Path.Combine(root, "first.wav");
        string second = Path.Combine(root, "second.wav");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(second, [4, 5, 6]);
        try
        {
            var repository = new FakeRepository();
            var transcription = new FakeTranscription();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(first, second), new AppPaths(root), NoopTranslator, CreateExporter(root));
            var observed = new List<double>();
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MediaWorkbenchViewModel.Progress)) observed.Add(viewModel.Progress);
            };

            await viewModel.InitializeAsync(CancellationToken.None);
            await viewModel.BrowseCommand.ExecuteAsync();
            await viewModel.StartCommand.ExecuteAsync();

            Assert.NotEmpty(observed);
            Assert.Equal(observed.Order().ToArray(), observed.ToArray());
            Assert.Equal(100, observed[^1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GroqJobExportsUtf8SrtAndPersistsHistoryInChineseSpacePath()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "中文 音频.wav");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        try
        {
            var repository = new FakeRepository();
            var dialog = new FakeDialogs(input);
            var transcription = new FakeTranscription();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(), dialog,
                new AppPaths(root), NoopTranslator, CreateExporter(root));
            await viewModel.InitializeAsync(CancellationToken.None);

            await viewModel.BrowseCommand.ExecuteAsync();
            await viewModel.StartCommand.ExecuteAsync();

            MediaJob completed = Assert.Single(repository.Jobs.Values);
            Assert.Equal(MediaJobStatus.Completed, completed.Status);
            Assert.NotNull(completed.OutputPath);
            Assert.True(File.Exists(completed.OutputPath));
            Assert.Contains("00:00:01,000 --> 00:00:02,000", await File.ReadAllTextAsync(completed.OutputPath));
            Assert.Equal("你好 Lenx", Assert.Single(repository.Segments[completed.Id]).Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TranslationFailurePersistsCompletedBatchAndNextRunResumesFromCheckpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "resume.srt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            input,
            "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n00:00:02,000 --> 00:00:03,000\nWorld\n\n",
            new UTF8Encoding(false));
        try
        {
            var repository = new FakeRepository();
            var translator = new ResumableTranslator();
            var paths = new AppPaths(root);
            var viewModel = new MediaWorkbenchViewModel(
                repository,
                repository,
                new FakeTranscription(),
                new FakeTranscription(),
                new FakeAudio(),
                new FakeModels(),
                new FakeDialogs(input),
                paths,
                translator,
                new SubtitleExportService(paths));

            await viewModel.InitializeAsync(CancellationToken.None);
            await viewModel.BrowseCommand.ExecuteAsync();
            viewModel.SelectedSubtitleJob = Assert.Single(viewModel.RecentJobs);
            await viewModel.SelectedSubtitleLoad;

            await viewModel.TranslateCommand.ExecuteAsync();

            MediaJob failed = repository.Jobs[viewModel.SelectedSubtitleJob.Id];
            Assert.Equal(MediaJobStatus.Failed, failed.Status);
            Assert.Equal(1, failed.TranslationNextSegmentIndex);
            Assert.Equal("你好", repository.Segments[failed.Id][0].TranslatedText);
            Assert.Null(repository.Segments[failed.Id][1].TranslatedText);

            await viewModel.TranslateCommand.ExecuteAsync();

            MediaJob completed = repository.Jobs[failed.Id];
            Assert.Equal(MediaJobStatus.Completed, completed.Status);
            Assert.Equal(2, completed.TranslationNextSegmentIndex);
            Assert.Equal(2, completed.AiRequestCount);
            Assert.Equal(30, completed.TranslationTotalTokens);
            Assert.Equal([0, 1], translator.ResumeIndexes);
            Assert.Equal("世界", repository.Segments[failed.Id][1].TranslatedText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelTranslationPersistsCancelledStateAndKeepsResumePosition()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var job = new MediaJob(
                "cancel-translation",
                "SubtitleImport",
                Path.Combine(root, "cancel.srt"),
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
            var repository = new FakeRepository(job);
            repository.Segments[job.Id] =
            [
                new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello") { Sequence = 1 }
            ];
            var translator = new BlockingTranslator();
            var paths = new AppPaths(root);
            var viewModel = new MediaWorkbenchViewModel(
                repository,
                repository,
                new FakeTranscription(),
                new FakeTranscription(),
                new FakeAudio(),
                new FakeModels(),
                new FakeDialogs(),
                paths,
                translator,
                new SubtitleExportService(paths));
            await viewModel.InitializeAsync(CancellationToken.None);
            await viewModel.SelectedSubtitleLoad;

            Task running = viewModel.TranslateCommand.ExecuteAsync();
            await translator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(viewModel.CancelTranslationCommand.CanExecute(null));
            viewModel.CancelTranslationCommand.Execute(null);
            await running;

            MediaJob cancelled = repository.Jobs[job.Id];
            Assert.Equal(MediaJobStatus.Cancelled, cancelled.Status);
            Assert.Equal(0, cancelled.TranslationNextSegmentIndex);
            Assert.True(cancelled.Error?.IsRetryable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrowseImportsBomSrtInChineseSpaceAndLongPathAndRestoresCompletedJob()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string longDirectory = Enumerable.Range(0, 8).Aggregate(
            Path.Combine(root, "中文 目录"),
            (current, index) => Path.Combine(current, $"第{index:00}层 空格目录 abcdefghijklmnop"));
        string input = Path.Combine(longDirectory, "已有 字幕.srt");
        Assert.True(input.Length > 260);
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        const string content = "7\r\n00:00:01,250 --> 00:00:03,500\r\n你好 Lenx\r\n\r\n";
        await File.WriteAllTextAsync(input, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var repository = new FakeRepository();
            var transcription = new FakeTranscription();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root), NoopTranslator, CreateExporter(root));

            await viewModel.InitializeAsync(CancellationToken.None);
            await viewModel.BrowseCommand.ExecuteAsync();

            MediaJob imported = Assert.Single(repository.Jobs.Values);
            Assert.Equal("SubtitleImport", imported.Kind);
            Assert.Equal(MediaJobStatus.Completed, imported.Status);
            Assert.Equal(TranscriptionEngine.ImportedSrt, imported.Engine);
            SubtitleSegment segment = Assert.Single(repository.Segments[imported.Id]);
            Assert.Equal(7, segment.Sequence);
            Assert.Equal("你好 Lenx", segment.Text);
            var reopened = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(), new AppPaths(root), NoopTranslator, CreateExporter(root));

            await reopened.InitializeAsync(CancellationToken.None);

            Assert.Equal(imported, Assert.Single(reopened.RecentJobs));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrowseMalformedSrtShowsLineNumberAndLeavesNoPersistedJob()
    {
        string root = Path.Combine(Path.GetTempPath(), "Lenx Tools tests", Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "坏 字幕.srt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            input,
            "1\nnot-a-time-range\ninvalid\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            var repository = new FakeRepository();
            var transcription = new FakeTranscription();
            var viewModel = new MediaWorkbenchViewModel(
                repository, repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root), NoopTranslator, CreateExporter(root));

            await viewModel.InitializeAsync(CancellationToken.None);
            await viewModel.BrowseCommand.ExecuteAsync();

            Assert.Empty(repository.Jobs);
            Assert.NotNull(viewModel.LastError);
            Assert.Equal(LenxTool.Core.Errors.AppErrorCode.InvalidRequest, viewModel.LastError.Code);
            Assert.Contains("第 2 行", viewModel.LastError.UserMessage, StringComparison.Ordinal);
            Assert.True(viewModel.BrowseCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeRepository(params MediaJob[] initialJobs) : IMediaJobRepository, ISubtitleRepository
    {
        public Dictionary<string, MediaJob> Jobs { get; } = initialJobs.ToDictionary(job => job.Id);
        public Dictionary<string, IReadOnlyList<SubtitleSegment>> Segments { get; } = [];
        public List<MediaJob> Snapshots { get; } = [];
        public Task UpsertAsync(MediaJob job, CancellationToken cancellationToken)
        {
            Jobs[job.Id] = job;
            Snapshots.Add(job);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<MediaJob>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaJob>>(Jobs.Values.ToArray());
        public Task<IReadOnlyList<MediaJob>> RecoverInterruptedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaJob>>(Jobs.Values.ToArray());
        public Task<IReadOnlyList<MediaJob>> GetQueuedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaJob>>(Jobs.Values.Where(job => job.Status == MediaJobStatus.Queued).ToArray());
        public Task CreateMediaJobWithSegmentsAsync(
            MediaJob job,
            IReadOnlyList<SubtitleSegment> segments,
            CancellationToken cancellationToken)
        {
            Jobs[job.Id] = job;
            Segments[job.Id] = segments.ToArray();
            Snapshots.Add(job);
            return Task.CompletedTask;
        }
        public Task ReplaceAsync(
            string mediaJobId,
            IReadOnlyList<SubtitleSegment> segments,
            CancellationToken cancellationToken)
        {
            Segments[mediaJobId] = segments.ToArray();
            return Task.CompletedTask;
        }
        public Task SaveTranslationBatchAsync(
            MediaJob job,
            IReadOnlyList<SubtitleSegment> segments,
            CancellationToken cancellationToken)
        {
            Jobs[job.Id] = job;
            Segments[job.Id] = segments.ToArray();
            Snapshots.Add(job);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SubtitleSegment>> GetByMediaJobIdAsync(
            string mediaJobId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Segments.GetValueOrDefault(mediaJobId) ?? []);
    }

    private sealed class ThrowingTranscription : ITranscriptionService, ILocalTranscriptionService
    {
        public Task<IReadOnlyList<SubtitleSegment>> TranscribeAsync(
            string audioPath, string model, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("transcription failed");
    }

    private sealed class FakeTranscription : ITranscriptionService, ILocalTranscriptionService
    {
        public Task<IReadOnlyList<SubtitleSegment>> TranscribeAsync(
            string audioPath, string model, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(37);
            progress?.Report(100);
            return Task.FromResult<IReadOnlyList<SubtitleSegment>>(
                [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "你好 Lenx")]);
        }
    }

    private sealed class FakeAudio : IMediaAudioService
    {
        public Task<PreparedAudio> PrepareAsync(string inputPath, CancellationToken cancellationToken) =>
            Task.FromResult(new PreparedAudio(inputPath, false, TimeSpan.FromSeconds(2)));
    }

    private sealed class FakeModels : ILocalModelService
    {
        public Task<LocalModelInfo> ImportAsync(string sourcePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<LocalModelInfo>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalModelInfo>>([]);
    }

    private sealed class FakeDialogs(params string[] inputs) : IDesktopFileDialogService
    {
        public IReadOnlyList<string> PickMediaFiles() => inputs;
        public string? PickWhisperModel() => null;
        public string? PickDatabaseBackup() => null;
        public string? PickFileForHash() => null;
        public (string Source, string Destination)? PickWordConversion() => null;
        public string? PickFolder() => null;
        public void OpenFolder(string path) { }
        public void OpenUri(string uri) { }
    }

    private static NoopSubtitleTranslator NoopTranslator { get; } = new();

    private static SubtitleExportService CreateExporter(string root) =>
        new SubtitleExportService(new AppPaths(root));

    private sealed class NoopSubtitleTranslator : ISubtitleTranslator
    {
        public async IAsyncEnumerable<SubtitleTranslationBatchResult> TranslateAsync(
            SubtitleTranslationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class ResumableTranslator : ISubtitleTranslator
    {
        private int _callCount;

        public List<int> ResumeIndexes { get; } = [];

        public async IAsyncEnumerable<SubtitleTranslationBatchResult> TranslateAsync(
            SubtitleTranslationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ResumeIndexes.Add(request.ResumeFrom.NextSegmentIndex);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            _callCount++;
            if (_callCount == 1)
            {
                yield return new(
                    new(request.OperationId, 1),
                    [new(1, "你好")],
                    request.Model,
                    1,
                    new(10, 5, 15));
                throw new SubtitleTranslationException(
                    new(
                        LenxTool.Core.Errors.AppErrorCode.ProviderUnavailable,
                        "翻译服务暂时不可用",
                        "已保存完成的字幕批次。",
                        "请重试以从断点继续。",
                        IsRetryable: true),
                    new(request.OperationId, 1));
            }

            yield return new(
                new(request.OperationId, 2),
                [new(2, "世界")],
                request.Model,
                1,
                new(10, 5, 15));
        }
    }

    private sealed class BlockingTranslator : ISubtitleTranslator
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<SubtitleTranslationBatchResult> TranslateAsync(
            SubtitleTranslationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
