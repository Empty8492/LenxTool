using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Media;

public sealed class SubtitleTranslationContractTests
{
    [Fact]
    public void RequestSnapshotsOnlySequenceAndOriginalTextWithBatchConfiguration()
    {
        SubtitleSegment[] segments =
        [
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "first", "旧译文")
            {
                Sequence = 7
            },
            new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "second")
            {
                Sequence = 9
            }
        ];

        SubtitleTranslationRequest request = SubtitleTranslationRequest.Create(
            "operation-1",
            "media-job-1",
            "简体中文",
            "deepseek-v4-flash",
            batchSize: 2,
            segments);

        Assert.Equal("operation-1", request.OperationId);
        Assert.Equal("media-job-1", request.MediaJobId);
        Assert.Equal("简体中文", request.TargetLanguage);
        Assert.Equal("deepseek-v4-flash", request.Model);
        Assert.Equal(2, request.BatchSize);
        Assert.Equal(new SubtitleTranslationCheckpoint("operation-1", 0), request.ResumeFrom);
        Assert.Equal(
            [new SubtitleTranslationInput(7, "first"), new SubtitleTranslationInput(9, "second")],
            request.Inputs);
    }

    [Fact]
    public void BatchResultAppliesOnlyTranslationsWithoutReorderingOrChangingSourceFields()
    {
        SubtitleSegment[] segments =
        [
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "first", null, -0.1, 0.01)
            {
                Sequence = 7
            },
            new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "second", null, -0.2, 0.02)
            {
                Sequence = 9
            }
        ];
        var result = new SubtitleTranslationBatchResult(
            new SubtitleTranslationCheckpoint("operation-1", 2),
            [
                new SubtitleTranslationItem(9, "第二"),
                new SubtitleTranslationItem(7, "第一")
            ],
            "deepseek-v4-flash",
            requestCount: 1,
            new SubtitleTokenUsage(100, 20, 120));

        IReadOnlyList<SubtitleSegment> translated = result.ApplyTo(segments);
        IReadOnlyList<SubtitleSegment> appliedAgain = result.ApplyTo(translated);

        Assert.Equal([7, 9], translated.Select(segment => segment.Sequence));
        Assert.Equal(["first", "second"], translated.Select(segment => segment.Text));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], translated.Select(segment => segment.Start));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], translated.Select(segment => segment.End));
        Assert.Equal([-0.1, -0.2], translated.Select(segment => segment.AverageLogProbability));
        Assert.Equal([0.01, 0.02], translated.Select(segment => segment.NoSpeechProbability));
        Assert.Equal(["第一", "第二"], translated.Select(segment => segment.TranslatedText));
        Assert.Equal(translated, appliedAgain);
    }

    [Fact]
    public void RequestRejectsInvalidBatchSizeAndMismatchedResumeOperation()
    {
        SubtitleSegment segment = new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "source")
        {
            Sequence = 1
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => SubtitleTranslationRequest.Create(
            "operation-1",
            "media-job-1",
            "English",
            "deepseek-v4-flash",
            SubtitleTranslationRequest.MaximumBatchSize + 1,
            [segment]));
        Assert.Throws<ArgumentException>(() => SubtitleTranslationRequest.Create(
            "operation-1",
            "media-job-1",
            "English",
            "deepseek-v4-flash",
            1,
            [segment],
            new SubtitleTranslationCheckpoint("another-operation", 0)));
    }

    [Fact]
    public void StructuredFailureCarriesRetryableErrorAndExactResumePosition()
    {
        var error = new AppError(
            AppErrorCode.ProviderRateLimited,
            "请求过于频繁",
            "DeepSeek 触发限流。",
            "稍后重试。",
            Provider: "DeepSeek",
            RetryAfter: TimeSpan.FromSeconds(30),
            IsRetryable: true);
        var checkpoint = new SubtitleTranslationCheckpoint("operation-1", 12);

        var exception = new SubtitleTranslationException(error, checkpoint);

        Assert.Same(error, exception.Error);
        Assert.Equal(checkpoint, exception.ResumeFrom);
        Assert.Equal(error.UserMessage, exception.Message);
    }

    [Fact]
    public void TranslatorContractStreamsRecoverableBatchesAndAcceptsCancellation()
    {
        var method = typeof(ISubtitleTranslator).GetMethod(nameof(ISubtitleTranslator.TranslateAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(IAsyncEnumerable<SubtitleTranslationBatchResult>), method.ReturnType);
        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(CancellationToken));
    }
}
