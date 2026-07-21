using LenxTool.Core.Errors;

namespace LenxTool.Core.Models;

public sealed record SubtitleTranslationInput(int Sequence, string Text);

public sealed record SubtitleTranslationItem(int Sequence, string TranslatedText);

/// <summary>
/// 一次幂等翻译操作的恢复点。位置是请求输入列表的从零开始索引，不是 SRT 原序号。
/// </summary>
public sealed record SubtitleTranslationCheckpoint
{
    public SubtitleTranslationCheckpoint(string operationId, int nextSegmentIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfNegative(nextSegmentIndex);
        OperationId = operationId;
        NextSegmentIndex = nextSegmentIndex;
    }

    public string OperationId { get; }

    public int NextSegmentIndex { get; }
}

public sealed record SubtitleTokenUsage
{
    public SubtitleTokenUsage(int promptTokens, int completionTokens, int totalTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(promptTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(completionTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(totalTokens);
        if (totalTokens < checked(promptTokens + completionTokens))
        {
            throw new ArgumentException("总 token 数不能小于输入与输出 token 数之和。", nameof(totalTokens));
        }

        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TotalTokens = totalTokens;
    }

    public int PromptTokens { get; }

    public int CompletionTokens { get; }

    public int TotalTokens { get; }

    public static SubtitleTokenUsage Zero { get; } = new(0, 0, 0);
}

public sealed record SubtitleTranslationRequest
{
    public const int MaximumBatchSize = 50;

    private SubtitleTranslationRequest(
        string operationId,
        string mediaJobId,
        string targetLanguage,
        string model,
        int batchSize,
        IReadOnlyList<SubtitleTranslationInput> inputs,
        SubtitleTranslationCheckpoint resumeFrom)
    {
        OperationId = operationId;
        MediaJobId = mediaJobId;
        TargetLanguage = targetLanguage;
        Model = model;
        BatchSize = batchSize;
        Inputs = inputs;
        ResumeFrom = resumeFrom;
    }

    public string OperationId { get; }

    public string MediaJobId { get; }

    public string TargetLanguage { get; }

    public string Model { get; }

    public int BatchSize { get; }

    public IReadOnlyList<SubtitleTranslationInput> Inputs { get; }

    public SubtitleTranslationCheckpoint ResumeFrom { get; }

    public static SubtitleTranslationRequest Create(
        string operationId,
        string mediaJobId,
        string targetLanguage,
        string model,
        int batchSize,
        IReadOnlyList<SubtitleSegment> segments,
        SubtitleTranslationCheckpoint? resumeFrom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaJobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(segments);
        if (batchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                batchSize,
                $"批大小必须在 1 到 {MaximumBatchSize} 之间。");
        }
        if (segments.Count == 0)
        {
            throw new ArgumentException("至少需要一个字幕片段。", nameof(segments));
        }

        var sequences = new HashSet<int>();
        var inputs = new SubtitleTranslationInput[segments.Count];
        for (int index = 0; index < segments.Count; index++)
        {
            SubtitleSegment segment = segments[index]
                ?? throw new ArgumentException("字幕片段不能为 null。", nameof(segments));
            if (segment.Sequence is not int sequence || sequence < 0)
            {
                throw new ArgumentException("每个字幕片段都必须具有非负原序号。", nameof(segments));
            }
            if (!sequences.Add(sequence))
            {
                throw new ArgumentException($"字幕原序号 {sequence} 重复。", nameof(segments));
            }
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                throw new ArgumentException($"字幕原序号 {sequence} 缺少原文。", nameof(segments));
            }
            inputs[index] = new(sequence, segment.Text);
        }

        SubtitleTranslationCheckpoint checkpoint = resumeFrom ?? new(operationId, 0);
        if (!string.Equals(checkpoint.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("恢复点不属于当前翻译操作。", nameof(resumeFrom));
        }
        if (checkpoint.NextSegmentIndex > inputs.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resumeFrom),
                checkpoint.NextSegmentIndex,
                "恢复位置超出字幕片段范围。");
        }

        return new(
            operationId,
            mediaJobId,
            targetLanguage,
            model,
            batchSize,
            Array.AsReadOnly(inputs),
            checkpoint);
    }
}

public sealed record SubtitleTranslationBatchResult
{
    public SubtitleTranslationBatchResult(
        SubtitleTranslationCheckpoint resumeFrom,
        IReadOnlyList<SubtitleTranslationItem> translations,
        string model,
        int requestCount,
        SubtitleTokenUsage tokenUsage)
    {
        ArgumentNullException.ThrowIfNull(resumeFrom);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestCount);
        ArgumentNullException.ThrowIfNull(tokenUsage);
        if (translations.Count == 0)
        {
            throw new ArgumentException("批次结果至少需要一条译文。", nameof(translations));
        }

        var sequences = new HashSet<int>();
        var snapshot = new SubtitleTranslationItem[translations.Count];
        for (int index = 0; index < translations.Count; index++)
        {
            SubtitleTranslationItem item = translations[index]
                ?? throw new ArgumentException("译文项不能为 null。", nameof(translations));
            ArgumentOutOfRangeException.ThrowIfNegative(item.Sequence);
            if (!sequences.Add(item.Sequence))
            {
                throw new ArgumentException($"译文原序号 {item.Sequence} 重复。", nameof(translations));
            }
            if (string.IsNullOrWhiteSpace(item.TranslatedText))
            {
                throw new ArgumentException($"译文原序号 {item.Sequence} 内容为空。", nameof(translations));
            }
            snapshot[index] = item;
        }

        ResumeFrom = resumeFrom;
        Translations = Array.AsReadOnly(snapshot);
        Model = model;
        RequestCount = requestCount;
        TokenUsage = tokenUsage;
    }

    /// <summary>当前批次持久化后，下次调用可安全使用的恢复点。</summary>
    public SubtitleTranslationCheckpoint ResumeFrom { get; }

    public IReadOnlyList<SubtitleTranslationItem> Translations { get; }

    public string Model { get; }

    public int RequestCount { get; }

    public SubtitleTokenUsage TokenUsage { get; }

    /// <summary>
    /// 按原序号填充译文并保持输入列表顺序；原文、时间轴和置信指标不会被修改。
    /// </summary>
    public IReadOnlyList<SubtitleSegment> ApplyTo(IReadOnlyList<SubtitleSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Dictionary<int, string> translatedTextBySequence = Translations.ToDictionary(
            item => item.Sequence,
            item => item.TranslatedText);
        var sourceSequences = new HashSet<int>();
        var result = new SubtitleSegment[segments.Count];
        for (int index = 0; index < segments.Count; index++)
        {
            SubtitleSegment segment = segments[index]
                ?? throw new ArgumentException("字幕片段不能为 null。", nameof(segments));
            if (segment.Sequence is not int sequence || !sourceSequences.Add(sequence))
            {
                throw new ArgumentException("字幕片段必须具有唯一原序号。", nameof(segments));
            }

            result[index] = translatedTextBySequence.TryGetValue(sequence, out string? translatedText)
                ? segment with { TranslatedText = translatedText }
                : segment;
            translatedTextBySequence.Remove(sequence);
        }

        if (translatedTextBySequence.Count > 0)
        {
            throw new ArgumentException(
                $"译文包含不存在的字幕原序号 {translatedTextBySequence.Keys.Min()}。",
                nameof(segments));
        }

        return result;
    }
}

public sealed class SubtitleTranslationException : Exception
{
    public SubtitleTranslationException(
        AppError error,
        SubtitleTranslationCheckpoint resumeFrom,
        Exception? innerException = null)
        : base(error?.UserMessage, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
        ResumeFrom = resumeFrom ?? throw new ArgumentNullException(nameof(resumeFrom));
    }

    public AppError Error { get; }

    public SubtitleTranslationCheckpoint ResumeFrom { get; }
}
