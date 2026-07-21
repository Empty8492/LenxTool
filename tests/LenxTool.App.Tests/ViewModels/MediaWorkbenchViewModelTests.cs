using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.App.Tests.ViewModels;

public sealed class MediaWorkbenchViewModelTests
{
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
                repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root));

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
                repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root));

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
                repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root));

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
                repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root));

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
                repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(input), new AppPaths(root));

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
                repository, transcription, transcription, new FakeAudio(), new FakeModels(),
                new FakeDialogs(first, second), new AppPaths(root));
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
                repository, transcription, transcription, new FakeAudio(), new FakeModels(), dialog,
                new AppPaths(root));
            await viewModel.InitializeAsync(CancellationToken.None);

            await viewModel.BrowseCommand.ExecuteAsync();
            await viewModel.StartCommand.ExecuteAsync();

            MediaJob completed = Assert.Single(repository.Jobs.Values);
            Assert.Equal(MediaJobStatus.Completed, completed.Status);
            Assert.NotNull(completed.OutputPath);
            Assert.True(File.Exists(completed.OutputPath));
            Assert.Contains("00:00:01,000 --> 00:00:02,000", await File.ReadAllTextAsync(completed.OutputPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeRepository(params MediaJob[] initialJobs) : IMediaJobRepository
    {
        public Dictionary<string, MediaJob> Jobs { get; } = initialJobs.ToDictionary(job => job.Id);
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
        public void OpenFolder(string path) { }
        public void OpenUri(string uri) { }
    }
}
