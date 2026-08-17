using LenxTool.App.Mvvm;
using LenxTool.Core.Tools;

namespace LenxTool.App.ViewModels;

public sealed partial class ToolsViewModel
{
    public const int MaximumDiffInputCharacters = 2 * 1024 * 1024;
    public const int MaximumDisplayedDifferences = 500;

    private const int MaximumValuePreviewCharacters = 2_048;

    private long _jsonDiffGeneration;
    private long _jsonDiffInputRevision;
    private string _leftJson = "{\n  \"name\": \"LenxTool\",\n  \"localFirst\": true\n}";
    private string _rightJson = "{\n  \"name\": \"LenxTool\",\n  \"localFirst\": false,\n  \"channel\": \"preview\"\n}";
    private string _leftJsonStatus = "左侧 JSON 待校验";
    private string _rightJsonStatus = "右侧 JSON 待校验";
    private string _jsonDiffStatus = "分别粘贴两份 JSON 后开始结构比较。";
    private string _jsonDiffEmptyText = "尚未运行结构比较。";
    private IReadOnlyList<JsonDifferenceRow> _differences = [];

    public string LeftJson
    {
        get => _leftJson;
        set
        {
            if (!SetProperty(ref _leftJson, value ?? string.Empty)) return;
            InvalidateJsonDiff("左侧输入已变化，请重新比较。");
        }
    }

    public string RightJson
    {
        get => _rightJson;
        set
        {
            if (!SetProperty(ref _rightJson, value ?? string.Empty)) return;
            InvalidateJsonDiff("右侧输入已变化，请重新比较。");
        }
    }

    public string LeftJsonStatus
    {
        get => _leftJsonStatus;
        private set => SetProperty(ref _leftJsonStatus, value);
    }

    public string RightJsonStatus
    {
        get => _rightJsonStatus;
        private set => SetProperty(ref _rightJsonStatus, value);
    }

    public string JsonDiffStatus
    {
        get => _jsonDiffStatus;
        private set => SetProperty(ref _jsonDiffStatus, value);
    }

    public string JsonDiffEmptyText
    {
        get => _jsonDiffEmptyText;
        private set => SetProperty(ref _jsonDiffEmptyText, value);
    }

    public IReadOnlyList<JsonDifferenceRow> Differences
    {
        get => _differences;
        private set
        {
            if (!SetProperty(ref _differences, value)) return;
            OnPropertyChanged(nameof(HasDifferences));
            OnPropertyChanged(nameof(ShowJsonDiffEmptyState));
        }
    }

    public bool HasDifferences => Differences.Count > 0;

    public bool ShowJsonDiffEmptyState => !HasDifferences;

    public bool IsJsonDiffRunning => CompareJsonCommand.IsRunning;

    public AsyncRelayCommand CompareJsonCommand { get; }

    public RelayCommand CancelJsonDiffCommand { get; }

    public RelayCommand SwapJsonSidesCommand { get; }

    private async Task CompareJsonAsync(CancellationToken cancellationToken)
    {
        long generation = Interlocked.Increment(ref _jsonDiffGeneration);
        long inputRevision = Volatile.Read(ref _jsonDiffInputRevision);
        string leftSnapshot = LeftJson;
        string rightSnapshot = RightJson;
        Differences = [];
        JsonDiffEmptyText = "正在比较 JSON 结构…";
        JsonDiffStatus = "正在后台校验并比较，两侧输入仍可编辑或取消。";

        try
        {
            // 比较只使用本次快照；编辑输入会取消旧任务，避免旧结果覆盖新内容。
            JsonDiffWorkResult result = await Task.Run(
                () => CompareJsonSnapshotsAsync(
                    leftSnapshot,
                    rightSnapshot,
                    cancellationToken),
                cancellationToken);

            // 后台任务可能已完成但 continuation 尚未回到 UI 线程；此处再次检查取消和代际，
            // 防止取消后的迟到结果，以及 A→B→A 回绕输入覆盖当前状态。
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _jsonDiffGeneration)
                || inputRevision != Volatile.Read(ref _jsonDiffInputRevision)
                || !string.Equals(leftSnapshot, LeftJson, StringComparison.Ordinal)
                || !string.Equals(rightSnapshot, RightJson, StringComparison.Ordinal))
            {
                JsonDiffStatus = "输入已变化，已丢弃旧比较结果。";
                JsonDiffEmptyText = "请对当前输入重新运行结构比较。";
                return;
            }

            LeftJsonStatus = result.LeftStatus;
            RightJsonStatus = result.RightStatus;
            if (!result.BothValid)
            {
                JsonDiffStatus = "请分别修正左右 JSON 后再比较。";
                JsonDiffEmptyText = "当前没有可显示的结构差异。";
                return;
            }

            Differences = result.Diff!.Differences
                .Select(JsonDifferenceRow.From)
                .ToArray();
            if (Differences.Count == 0)
            {
                JsonDiffStatus = "左右 JSON 结构和值完全一致。";
                JsonDiffEmptyText = "未发现结构差异。";
                return;
            }

            JsonDiffStatus = result.Diff.IsTruncated
                ? $"发现至少 {Differences.Count} 处差异；已按结果预算截断，显示前 {Differences.Count} 处（数量上限 {MaximumDisplayedDifferences}）。"
                : $"发现 {Differences.Count} 处差异。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool inputChanged = inputRevision != Volatile.Read(ref _jsonDiffInputRevision)
                || !string.Equals(
                    leftSnapshot,
                    LeftJson,
                    StringComparison.Ordinal)
                || !string.Equals(
                    rightSnapshot,
                    RightJson,
                    StringComparison.Ordinal);
            JsonDiffStatus = inputChanged
                ? "输入已变化，旧比较已取消。"
                : "JSON 结构比较已取消。";
            JsonDiffEmptyText = "没有保留不完整的比较结果。";
        }
    }

    private static async Task<JsonDiffWorkResult> CompareJsonSnapshotsAsync(
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        JsonDiffAnalysisResult analysis = await JsonToolkit.AnalyzeDiffAsync(
            left,
            right,
            MaximumDisplayedDifferences,
            MaximumDiffInputCharacters,
            cancellationToken).ConfigureAwait(false);
        return new(
            DescribeValidation("左侧", analysis.LeftValidation),
            DescribeValidation("右侧", analysis.RightValidation),
            analysis.Diff);
    }

    private static string DescribeValidation(
        string side,
        JsonValidationResult validation)
    {
        if (validation.IsValid) return $"{side} JSON 语法有效";
        if (validation.LineNumber is not null
            && validation.BytePositionInLine is not null)
        {
            return $"{side} JSON 无效：第 {validation.LineNumber + 1} 行，行内 UTF-8 字节位置 {validation.BytePositionInLine + 1}";
        }

        return $"{side} JSON 无效：{validation.Message ?? "输入不符合 JSON 语法"}";
    }

    private void CancelJsonDiff()
    {
        if (!CompareJsonCommand.IsRunning) return;
        Interlocked.Increment(ref _jsonDiffGeneration);
        JsonDiffStatus = "正在取消 JSON 结构比较…";
        CompareJsonCommand.Cancel();
    }

    private void SwapJsonSides()
    {
        Interlocked.Increment(ref _jsonDiffGeneration);
        Interlocked.Increment(ref _jsonDiffInputRevision);
        CompareJsonCommand.Cancel();
        (_leftJson, _rightJson) = (_rightJson, _leftJson);
        OnPropertyChanged(nameof(LeftJson));
        OnPropertyChanged(nameof(RightJson));
        ResetJsonDiffState("左右 JSON 已交换，请重新比较。");
    }

    private void InvalidateJsonDiff(string status)
    {
        Interlocked.Increment(ref _jsonDiffGeneration);
        Interlocked.Increment(ref _jsonDiffInputRevision);
        CompareJsonCommand.Cancel();
        ResetJsonDiffState(status);
    }

    private void ResetJsonDiffState(string status)
    {
        LeftJsonStatus = "左侧 JSON 待校验";
        RightJsonStatus = "右侧 JSON 待校验";
        Differences = [];
        JsonDiffStatus = status;
        JsonDiffEmptyText = "请运行结构比较以查看差异。";
    }

    private sealed record JsonDiffWorkResult(
        string LeftStatus,
        string RightStatus,
        JsonDiffResult? Diff)
    {
        public bool BothValid => Diff is not null;
    }

    public sealed record JsonDifferenceRow(
        string Path,
        string KindText,
        string LeftValue,
        string RightValue)
    {
        public static JsonDifferenceRow From(JsonDifference difference) => new(
            difference.Path,
            difference.Kind switch
            {
                JsonDifferenceKind.Added => "新增",
                JsonDifferenceKind.Removed => "删除",
                JsonDifferenceKind.Changed => "修改",
                _ => "未知"
            },
            Preview(difference.LeftValue),
            Preview(difference.RightValue));

        private static string Preview(string? value)
        {
            if (value is null) return "—";
            return value.Length <= MaximumValuePreviewCharacters
                ? value
                : string.Concat(
                    value.AsSpan(0, MaximumValuePreviewCharacters),
                    "…");
        }
    }
}
