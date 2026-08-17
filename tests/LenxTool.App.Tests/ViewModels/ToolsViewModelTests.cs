using System.Collections.Concurrent;
using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;

namespace LenxTool.App.Tests.ViewModels;

public sealed class ToolsViewModelTests
{
    [Fact]
    public async Task CompareJsonValidatesBothSidesWithoutOverwritingTheOtherError()
    {
        ToolsViewModel viewModel = CreateViewModel();
        viewModel.LeftJson = "{\"broken\":}";
        viewModel.RightJson = "{\"valid\":true}";

        await viewModel.CompareJsonCommand.ExecuteAsync();

        Assert.Contains("左侧 JSON 无效", viewModel.LeftJsonStatus, StringComparison.Ordinal);
        Assert.Equal("右侧 JSON 语法有效", viewModel.RightJsonStatus);
        Assert.Empty(viewModel.Differences);
        Assert.Contains("修正", viewModel.JsonDiffStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareJsonShowsKindsPathsValuesAndSwapsInputs()
    {
        ToolsViewModel viewModel = CreateViewModel();
        viewModel.LeftJson = "{\"changed\":1,\"removed\":2}";
        viewModel.RightJson = "{\"added\":3,\"changed\":4}";

        await viewModel.CompareJsonCommand.ExecuteAsync();

        Assert.Contains(
            viewModel.Differences,
            item => item.Path == "$[\"added\"]"
                && item.KindText == "新增"
                && item.LeftValue == "—"
                && item.RightValue == "3");
        Assert.Contains(
            viewModel.Differences,
            item => item.Path == "$[\"removed\"]"
                && item.KindText == "删除"
                && item.LeftValue == "2"
                && item.RightValue == "—");
        Assert.Contains(
            viewModel.Differences,
            item => item.Path == "$[\"changed\"]"
                && item.KindText == "修改"
                && item.LeftValue == "1"
                && item.RightValue == "4");
        Assert.Contains("3 处差异", viewModel.JsonDiffStatus, StringComparison.Ordinal);

        viewModel.SwapJsonSidesCommand.Execute(null);

        Assert.Equal("{\"added\":3,\"changed\":4}", viewModel.LeftJson);
        Assert.Equal("{\"changed\":1,\"removed\":2}", viewModel.RightJson);
        Assert.Empty(viewModel.Differences);
        Assert.Contains("已交换", viewModel.JsonDiffStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareJsonReportsTheActualCountWhenPathBudgetTruncatesEarly()
    {
        ToolsViewModel viewModel = CreateViewModel();
        string prefix = new('p', 700);
        viewModel.LeftJson = "{" + string.Join(
            ',',
            Enumerable.Range(0, ToolsViewModel.MaximumDisplayedDifferences)
                .Select(index => $"\"{prefix}{index:D3}\":0")) + "}";
        viewModel.RightJson = "{" + string.Join(
            ',',
            Enumerable.Range(0, ToolsViewModel.MaximumDisplayedDifferences)
                .Select(index => $"\"{prefix}{index:D3}\":1")) + "}";

        await viewModel.CompareJsonCommand.ExecuteAsync();

        Assert.InRange(
            viewModel.Differences.Count,
            1,
            ToolsViewModel.MaximumDisplayedDifferences - 1);
        Assert.Contains(
            $"显示前 {viewModel.Differences.Count} 处",
            viewModel.JsonDiffStatus,
            StringComparison.Ordinal);
        Assert.Contains(
            $"数量上限 {ToolsViewModel.MaximumDisplayedDifferences}",
            viewModel.JsonDiffStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareJsonRejectsOversizedSideAndStillValidatesTheOtherSide()
    {
        ToolsViewModel viewModel = CreateViewModel();
        viewModel.LeftJson = new string(' ', ToolsViewModel.MaximumDiffInputCharacters + 1);
        viewModel.RightJson = "{\"valid\":true}";

        await viewModel.CompareJsonCommand.ExecuteAsync();

        Assert.Contains("超过", viewModel.LeftJsonStatus, StringComparison.Ordinal);
        Assert.Equal("右侧 JSON 语法有效", viewModel.RightJsonStatus);
        Assert.Empty(viewModel.Differences);
    }

    [Fact]
    public async Task CompareJsonLabelsUtf8BytePositionAccurately()
    {
        ToolsViewModel viewModel = CreateViewModel();
        viewModel.LeftJson = "{\"中文\": }";
        viewModel.RightJson = "{}";

        await viewModel.CompareJsonCommand.ExecuteAsync();

        Assert.Contains(
            "行内 UTF-8 字节位置",
            viewModel.LeftJsonStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CancelAfterBackgroundCompletionDoesNotPublishLateResults()
    {
        ToolsViewModel viewModel = CreateViewModel();
        viewModel.LeftJson = "{\"value\":1}";
        viewModel.RightJson = "{\"value\":2}";
        using var context = new QueuedSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            Task operation = viewModel.CompareJsonCommand.ExecuteAsync();
            Assert.True(context.WaitForPost(TimeSpan.FromSeconds(5)));

            viewModel.CancelJsonDiffCommand.Execute(null);
            context.RunUntilCompleted(operation, TimeSpan.FromSeconds(5));

            Assert.Empty(viewModel.Differences);
            Assert.Contains(
                "已取消",
                viewModel.JsonDiffStatus,
                StringComparison.Ordinal);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void InputAbaChangeDoesNotPublishTheOldGeneration()
    {
        ToolsViewModel viewModel = CreateViewModel();
        viewModel.LeftJson = "{\"value\":1}";
        viewModel.RightJson = "{\"value\":2}";
        string originalLeft = viewModel.LeftJson;
        using var context = new QueuedSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            Task operation = viewModel.CompareJsonCommand.ExecuteAsync();
            Assert.True(context.WaitForPost(TimeSpan.FromSeconds(5)));

            viewModel.LeftJson = "{\"temporary\":true}";
            viewModel.LeftJson = originalLeft;
            context.RunUntilCompleted(operation, TimeSpan.FromSeconds(5));

            Assert.Empty(viewModel.Differences);
            Assert.Contains(
                "输入已变化",
                viewModel.JsonDiffStatus,
                StringComparison.Ordinal);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static ToolsViewModel CreateViewModel() => new(
        new StubFileHashService(),
        new StubDocumentConverter(),
        new StubDialogs());

    private sealed class StubFileHashService : IFileHashService
    {
        public Task<string> ComputeSha256Async(
            string filePath,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);
    }

    private sealed class StubDocumentConverter : IDocumentConverter
    {
        public string Name => "Stub";
        public bool IsAvailable => false;

        public Task ConvertToPdfAsync(
            string sourcePath,
            string destinationPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubDialogs : IDesktopFileDialogService
    {
        public IReadOnlyList<string> PickMediaFiles() => [];
        public string? PickWhisperModel() => null;
        public string? PickDatabaseBackup() => null;
        public string? PickFileForHash() => null;
        public (string Source, string Destination)? PickWordConversion() => null;
        public string? PickFolder() => null;
        public void OpenFolder(string path) { }
        public void OpenUri(string uri) { }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = [];
        private readonly AutoResetEvent _posted = new(initialState: false);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            _posted.Set();
        }

        public bool WaitForPost(TimeSpan timeout) =>
            !_callbacks.IsEmpty || _posted.WaitOne(timeout);

        public void RunUntilCompleted(Task task, TimeSpan timeout)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (stopwatch.Elapsed > timeout)
                {
                    throw new TimeoutException(
                        "等待排队的 JSON Diff continuation 超时。");
                }

                if (_callbacks.TryDequeue(out var work))
                {
                    work.Callback(work.State);
                    continue;
                }

                _posted.WaitOne(TimeSpan.FromMilliseconds(20));
            }

            task.GetAwaiter().GetResult();
        }

        public void Dispose() => _posted.Dispose();
    }
}
