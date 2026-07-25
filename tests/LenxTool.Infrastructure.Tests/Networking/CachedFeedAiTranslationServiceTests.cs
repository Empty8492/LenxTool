using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class CachedFeedAiTranslationServiceTests
{
    [Fact]
    public async Task CompletedExactCacheRestoresBlocksWithoutCallingTranslatorOrChangingLinks()
    {
        FeedAiTranslationInput input = CreateInput();
        FeedAiCacheKey key = CreateKey(input);
        var repository = new StubFeedAiResultRepository();
        FeedAiResult cached = CreateCachedResult(
            key,
            """
            {"version":1,"operationId":"cached-operation","nextBlockIndex":2,"translations":[{"sequence":10,"translatedText":"第一段"},{"sequence":20,"translatedText":"第二段"}]}
            """);
        repository.Stored[key] = cached;
        var translator = new StubSubtitleTranslator((_, _, _) =>
            throw new InvalidOperationException("完整缓存命中不应调用翻译器。"));
        var service = CreateService(translator, repository);

        FeedAiTranslationResult result = await service.TranslateAsync(
            input,
            CancellationToken.None);

        Assert.Same(cached, result.CacheRecord);
        Assert.Equal(0, translator.CallCount);
        Assert.Equal([10, 20], result.Blocks.Select(block => block.Sequence));
        Assert.Equal(["第一段", "第二段"], result.Blocks.Select(block => block.TranslatedText));
        Assert.Equal(
            input.Blocks.SelectMany(block => block.Links).Select(link => link.Url),
            result.Blocks.SelectMany(block => block.Links).Select(link => link.Url));
        Assert.Equal(
            input.Blocks.Select(block => block.ResourceUrl),
            result.Blocks.Select(block => block.ResourceUrl));
    }

    [Fact]
    public async Task TranslationPersistsEachBatchAndNormalizesProviderOrder()
    {
        FeedAiTranslationInput input = CreateInput(includeThirdBlock: true);
        var repository = new StubFeedAiResultRepository();
        var translator = new StubSubtitleTranslator((request, _, _) =>
        {
            Assert.Equal("简体中文", request.TargetLanguage);
            Assert.Equal("deepseek-v4-flash", request.Model);
            Assert.Equal(0, request.ResumeFrom.NextSegmentIndex);
            Assert.Equal(
                [
                    new SubtitleTranslationInput(10, "First <script>ignore()</script>"),
                    new SubtitleTranslationInput(20, "Second"),
                    new SubtitleTranslationInput(30, "Third")
                ],
                request.Inputs);
            Assert.DoesNotContain(
                "https://example.com/private-link",
                request.Inputs.Select(item => item.Text));

            return Yield(
                new(
                    new(request.OperationId, 2),
                    [
                        new SubtitleTranslationItem(20, "第二段"),
                        new SubtitleTranslationItem(10, "<b>第一段</b>")
                    ],
                    request.Model,
                    1,
                    new(80, 20, 100)),
                new(
                    new(request.OperationId, 3),
                    [new SubtitleTranslationItem(30, "第三段")],
                    request.Model,
                    1,
                    new(40, 10, 50)));
        });
        var service = CreateService(translator, repository);

        FeedAiTranslationResult result = await service.TranslateAsync(
            input,
            CancellationToken.None);

        Assert.Equal([10, 20, 30], result.Blocks.Select(block => block.Sequence));
        Assert.Equal(
            ["<b>第一段</b>", "第二段", "第三段"],
            result.Blocks.Select(block => block.TranslatedText));
        Assert.Equal(
            ["First <script>ignore()</script>", "Second", "Third"],
            result.Blocks.Select(block => block.OriginalText));
        Assert.Equal(2, repository.Upserts.Count);
        Assert.Equal("TranslationInProgress", repository.Upserts[0].ErrorCode);
        Assert.Null(repository.Upserts[1].ErrorCode);
        Assert.Equal(2, result.CacheRecord.RequestCount);
        Assert.Equal(120, result.CacheRecord.PromptTokens);
        Assert.Equal(30, result.CacheRecord.CompletionTokens);
        Assert.Equal(150, result.CacheRecord.TotalTokens);
        Assert.DoesNotContain(
            "https://example.com/private-link",
            result.CacheRecord.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedLongTranslationResumesFromPersistedBatch()
    {
        FeedAiTranslationInput input = CreateInput(includeThirdBlock: true);
        var repository = new StubFeedAiResultRepository();
        var translator = new StubSubtitleTranslator((request, call, _) =>
            call == 1
                ? YieldThenFail(
                    new(
                        new(request.OperationId, 2),
                        [
                            new SubtitleTranslationItem(10, "第一段"),
                            new SubtitleTranslationItem(20, "第二段")
                        ],
                        request.Model,
                        1,
                        new(70, 20, 90)),
                    new SubtitleTranslationException(
                        RateLimitedError(),
                        new(request.OperationId, 2)))
                : AssertResumeAndYieldLast(request));
        var service = CreateService(translator, repository);

        AppException failure = await Assert.ThrowsAsync<AppException>(() =>
            service.TranslateAsync(input, CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderRateLimited, failure.Error.Code);
        FeedAiResult partial = repository.Stored[CreateKey(input)];
        Assert.Equal(nameof(AppErrorCode.ProviderRateLimited), partial.ErrorCode);
        Assert.Contains("\"nextBlockIndex\":2", partial.Content, StringComparison.Ordinal);

        FeedAiTranslationResult completed = await service.TranslateAsync(
            input,
            CancellationToken.None);

        Assert.Equal(2, translator.CallCount);
        Assert.Equal(2, translator.Requests[1].ResumeFrom.NextSegmentIndex);
        Assert.Equal(["第一段", "第二段", "第三段"], completed.Blocks.Select(block => block.TranslatedText));
        Assert.Null(completed.CacheRecord.ErrorCode);
        Assert.Equal(2, completed.CacheRecord.RequestCount);
        Assert.Equal(110, completed.CacheRecord.PromptTokens);
        Assert.Equal(30, completed.CacheRecord.CompletionTokens);
        Assert.Equal(140, completed.CacheRecord.TotalTokens);
    }

    [Fact]
    public async Task MissingBatchItemIsRejectedAndOriginalBlocksRemainReadable()
    {
        FeedAiTranslationInput input = CreateInput();
        FeedAiTranslationBlock[] originalSnapshot = input.Blocks.ToArray();
        var repository = new StubFeedAiResultRepository();
        var translator = new StubSubtitleTranslator((request, _, _) =>
            Yield(
                new SubtitleTranslationBatchResult(
                    new(request.OperationId, 2),
                    [new SubtitleTranslationItem(10, "只有第一段")],
                    request.Model,
                    1,
                    new(20, 5, 25))));
        var service = CreateService(translator, repository);

        AppException failure = await Assert.ThrowsAsync<AppException>(() =>
            service.TranslateAsync(input, CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderUnavailable, failure.Error.Code);
        Assert.Equal(originalSnapshot, input.Blocks);
        FeedAiResult cachedFailure = repository.Stored[CreateKey(input)];
        Assert.Equal(nameof(AppErrorCode.ProviderUnavailable), cachedFailure.ErrorCode);
        Assert.Contains("\"nextBlockIndex\":0", cachedFailure.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationKeepsCompletedBatchForNextAttempt()
    {
        FeedAiTranslationInput input = CreateInput(includeThirdBlock: true);
        var repository = new StubFeedAiResultRepository();
        using var cancellation = new CancellationTokenSource();
        var translator = new StubSubtitleTranslator((request, call, _) =>
            call == 1
                ? YieldThenCancel(
                    new(
                        new(request.OperationId, 2),
                        [
                            new SubtitleTranslationItem(10, "第一段"),
                            new SubtitleTranslationItem(20, "第二段")
                        ],
                        request.Model,
                        1,
                        new(70, 20, 90)),
                    cancellation)
                : AssertResumeAndYieldLast(request));
        var service = CreateService(translator, repository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TranslateAsync(input, cancellation.Token));

        FeedAiResult partial = repository.Stored[CreateKey(input)];
        Assert.Equal("TranslationInProgress", partial.ErrorCode);
        Assert.Contains("\"nextBlockIndex\":2", partial.Content, StringComparison.Ordinal);

        FeedAiTranslationResult completed = await service.TranslateAsync(
            input,
            CancellationToken.None);

        Assert.Equal(2, translator.Requests[1].ResumeFrom.NextSegmentIndex);
        Assert.Equal(["第一段", "第二段", "第三段"], completed.Blocks.Select(block => block.TranslatedText));
    }

    [Fact]
    public async Task TargetLanguageModelAndContentHashArePartOfExactCacheIdentity()
    {
        var repository = new StubFeedAiResultRepository();
        var translator = new StubSubtitleTranslator((request, _, _) =>
            Yield(
                new SubtitleTranslationBatchResult(
                    new(request.OperationId, request.Inputs.Count),
                    request.Inputs
                        .Select(item => new SubtitleTranslationItem(
                            item.Sequence,
                            $"{request.TargetLanguage}:{item.Text}"))
                        .ToArray(),
                    request.Model,
                    1,
                    new(10, 5, 15))));
        var service = CreateService(translator, repository);
        FeedAiTranslationInput original = CreateInput();

        await service.TranslateAsync(original, CancellationToken.None);
        await service.TranslateAsync(original, CancellationToken.None);
        await service.TranslateAsync(
            original with { TargetLanguage = "English" },
            CancellationToken.None);
        await service.TranslateAsync(
            original with { ContentHash = new string('b', 64) },
            CancellationToken.None);
        var alternateModelService = CreateService(
            translator,
            repository,
            FeedAiTranslationOptions.Default with { Model = "deepseek-chat" });
        await alternateModelService.TranslateAsync(original, CancellationToken.None);

        Assert.Equal(4, translator.CallCount);
        Assert.Equal(4, repository.Stored.Keys.Count(key =>
            key.TaskType == FeedAiTaskType.Translation));
    }

    [Fact]
    public async Task ConcurrentExactRequestsShareOneTranslationOperation()
    {
        var repository = new StubFeedAiResultRepository();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var translator = new StubSubtitleTranslator((request, _, cancellationToken) =>
            WaitAndYield(request, started, release, cancellationToken));
        var service = CreateService(translator, repository);
        FeedAiTranslationInput input = CreateInput();

        Task<FeedAiTranslationResult> first = service.TranslateAsync(
            input,
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<FeedAiTranslationResult> second = service.TranslateAsync(
            input,
            CancellationToken.None);
        release.TrySetResult();
        FeedAiTranslationResult[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, translator.CallCount);
        Assert.Equal(results[0].CacheRecord, results[1].CacheRecord);
        Assert.Equal(
            results[0].Blocks.Select(block => block.TranslatedText),
            results[1].Blocks.Select(block => block.TranslatedText));
    }

    private static CachedFeedAiTranslationService CreateService(
        ISubtitleTranslator translator,
        IFeedAiResultRepository repository,
        FeedAiTranslationOptions? options = null) =>
        new(
            translator,
            repository,
            TimeProvider.System,
            options ?? FeedAiTranslationOptions.Default);

    private static FeedAiTranslationInput CreateInput(bool includeThirdBlock = false)
    {
        var privateLink = new ArticleContentLink(
            "https://example.com/private-link",
            "source link");
        var blocks = new List<FeedAiTranslationBlock>
        {
            new(
                10,
                FeedAiTranslationBlockKind.Paragraph,
                "First <script>ignore()</script>",
                null,
                null,
                [privateLink]),
            new(
                20,
                FeedAiTranslationBlockKind.Paragraph,
                "Second",
                "https://example.com/image.png",
                null,
                [])
        };
        if (includeThirdBlock)
        {
            blocks.Add(new(
                30,
                FeedAiTranslationBlockKind.Quote,
                "Third",
                null,
                null,
                []));
        }
        return new(
            "entry-1",
            new string('a', 64),
            "Article title",
            "简体中文",
            blocks);
    }

    private static FeedAiCacheKey CreateKey(FeedAiTranslationInput input) =>
        new(
            input.EntryId,
            input.ContentHash,
            FeedAiTaskType.Translation,
            input.TargetLanguage,
            FeedAiTranslationOptions.Default.Model,
            FeedAiTranslationOptions.Default.PromptVersion);

    private static FeedAiResult CreateCachedResult(FeedAiCacheKey key, string content)
    {
        DateTimeOffset now = DateTimeOffset.Parse(
            "2026-07-25T03:00:00Z",
            CultureInfo.InvariantCulture);
        return new(
            "feed-ai-cached",
            key,
            "Article title",
            content,
            1,
            10,
            5,
            15,
            100,
            null,
            now,
            now);
    }

    private static AppError RateLimitedError() =>
        new(
            AppErrorCode.ProviderRateLimited,
            "请求过于频繁",
            "DeepSeek 触发限流。",
            "稍后重试。",
            Provider: "DeepSeek",
            RetryAfter: TimeSpan.FromSeconds(10),
            IsRetryable: true);

    private static async IAsyncEnumerable<SubtitleTranslationBatchResult> Yield(
        params SubtitleTranslationBatchResult[] batches)
    {
        foreach (SubtitleTranslationBatchResult batch in batches)
        {
            await Task.Yield();
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<SubtitleTranslationBatchResult> YieldThenFail(
        SubtitleTranslationBatchResult batch,
        Exception exception)
    {
        await Task.Yield();
        yield return batch;
        throw exception;
    }

    private static async IAsyncEnumerable<SubtitleTranslationBatchResult> YieldThenCancel(
        SubtitleTranslationBatchResult batch,
        CancellationTokenSource cancellation)
    {
        await Task.Yield();
        yield return batch;
        cancellation.Cancel();
        throw new SubtitleTranslationException(
            new(
                AppErrorCode.OperationCancelled,
                "翻译已取消",
                "当前批次未写入。",
                "可继续翻译。",
                Provider: "DeepSeek",
                IsRetryable: true),
            batch.ResumeFrom);
    }

    private static async IAsyncEnumerable<SubtitleTranslationBatchResult> WaitAndYield(
        SubtitleTranslationRequest request,
        TaskCompletionSource started,
        TaskCompletionSource release,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        yield return new(
            new(request.OperationId, request.Inputs.Count),
            request.Inputs
                .Select(item => new SubtitleTranslationItem(item.Sequence, $"译文:{item.Text}"))
                .ToArray(),
            request.Model,
            1,
            new(10, 5, 15));
    }

    private static IAsyncEnumerable<SubtitleTranslationBatchResult> AssertResumeAndYieldLast(
        SubtitleTranslationRequest request)
    {
        Assert.Equal(2, request.ResumeFrom.NextSegmentIndex);
        return Yield(
            new SubtitleTranslationBatchResult(
                new(request.OperationId, 3),
                [new SubtitleTranslationItem(30, "第三段")],
                request.Model,
                1,
                new(40, 10, 50)));
    }

    private sealed class StubSubtitleTranslator(
        Func<
            SubtitleTranslationRequest,
            int,
            CancellationToken,
            IAsyncEnumerable<SubtitleTranslationBatchResult>> translate)
        : ISubtitleTranslator
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public List<SubtitleTranslationRequest> Requests { get; } = [];

        public IAsyncEnumerable<SubtitleTranslationBatchResult> TranslateAsync(
            SubtitleTranslationRequest request,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);
            lock (Requests)
            {
                Requests.Add(request);
            }
            return translate(request, call, cancellationToken);
        }
    }

    private sealed class StubFeedAiResultRepository : IFeedAiResultRepository
    {
        public ConcurrentDictionary<FeedAiCacheKey, FeedAiResult> Stored { get; } = [];

        public List<FeedAiResult> Upserts { get; } = [];

        public Task UpsertAsync(FeedAiResult result, CancellationToken cancellationToken)
        {
            Stored[result.CacheKey] = result;
            lock (Upserts)
            {
                Upserts.Add(result);
            }
            return Task.CompletedTask;
        }

        public Task<FeedAiResult?> GetCurrentAsync(
            FeedAiCacheKey key,
            CancellationToken cancellationToken) =>
            Task.FromResult(Stored.GetValueOrDefault(key));

        public Task<IReadOnlyList<FeedAiResult>> GetHistoryAsync(
            string entryId,
            FeedAiTaskType taskType,
            string targetLanguage,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FeedAiResult>>(
                Stored.Values
                    .Where(item =>
                        item.CacheKey.EntryId == entryId
                        && item.CacheKey.TaskType == taskType
                        && item.CacheKey.TargetLanguage == targetLanguage)
                    .Take(limit)
                    .ToArray());
    }
}
