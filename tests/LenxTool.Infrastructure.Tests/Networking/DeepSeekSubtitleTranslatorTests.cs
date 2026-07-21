using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class DeepSeekSubtitleTranslatorTests
{
    [Fact]
    public async Task TranslateAsyncReordersModelItemsAndReportsUsage()
    {
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            using JsonDocument body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            JsonElement root = body.RootElement;
            Assert.Equal("deepseek-v4-flash", root.GetProperty("model").GetString());
            Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
            Assert.DoesNotContain("00:00", root.GetRawText(), StringComparison.Ordinal);
            return Response("""
                {"translations":[
                  {"sequence":9,"translatedText":"第二"},
                  {"sequence":7,"translatedText":"第一"}
                ]}
                """, promptTokens: 80, completionTokens: 20);
        });
        DeepSeekSubtitleTranslator translator = Create(handler);
        SubtitleTranslationRequest request = Request(batchSize: 2);
        var results = new List<SubtitleTranslationBatchResult>();

        await foreach (SubtitleTranslationBatchResult result in translator.TranslateAsync(
                           request,
                           CancellationToken.None))
        {
            results.Add(result);
        }

        SubtitleTranslationBatchResult batch = Assert.Single(results);
        Assert.Equal([7, 9], batch.Translations.Select(item => item.Sequence));
        Assert.Equal(["第一", "第二"], batch.Translations.Select(item => item.TranslatedText));
        Assert.Equal(new SubtitleTranslationCheckpoint("operation-1", 2), batch.ResumeFrom);
        Assert.Equal(new SubtitleTokenUsage(80, 20, 100), batch.TokenUsage);
    }

    [Fact]
    public async Task TranslateAsyncRejectsMissingItemAtCurrentCheckpoint()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Response(
            """{"translations":[{"sequence":7,"translatedText":"第一"}]}""",
            20,
            10)));
        DeepSeekSubtitleTranslator translator = Create(handler);

        SubtitleTranslationException exception = await Assert.ThrowsAsync<SubtitleTranslationException>(async () =>
        {
            await foreach (SubtitleTranslationBatchResult _ in translator.TranslateAsync(
                               Request(batchSize: 2),
                               CancellationToken.None)) { }
        });

        Assert.Equal(AppErrorCode.ProviderUnavailable, exception.Error.Code);
        Assert.Equal(new SubtitleTranslationCheckpoint("operation-1", 0), exception.ResumeFrom);
    }

    [Fact]
    public async Task TranslateAsyncRetriesRateLimitAndCountsEveryRequest()
    {
        int requestCount = 0;
        var handler = new StubHandler((_, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":\"slow down\"}", Encoding.UTF8, "application/json")
                };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            }

            return Task.FromResult(Response(
                """{"translations":[{"sequence":7,"translatedText":"第一"},{"sequence":9,"translatedText":"第二"}]}""",
                30,
                10));
        });
        DeepSeekSubtitleTranslator translator = Create(handler);

        SubtitleTranslationBatchResult? result = null;
        await foreach (SubtitleTranslationBatchResult batch in translator.TranslateAsync(
                           Request(batchSize: 2),
                           CancellationToken.None))
        {
            result = batch;
        }

        Assert.NotNull(result);
        Assert.Equal(2, requestCount);
        Assert.Equal(2, result.RequestCount);
        Assert.Equal(new SubtitleTranslationCheckpoint("operation-1", 2), result.ResumeFrom);
    }

    [Fact]
    public async Task TranslateAsyncBoundsUntrustedRetryAfterHeader()
    {
        int requestCount = 0;
        var handler = new StubHandler((_, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation(
                    "Retry-After",
                    "999999999999999999999999999999999999999999999999999999999999");
                return Task.FromResult(response);
            }

            return Task.FromResult(Response(
                """{"translations":[{"sequence":7,"translatedText":"第一"},{"sequence":9,"translatedText":"第二"}]}""",
                30,
                10));
        });
        DeepSeekSubtitleTranslator translator = Create(handler);

        SubtitleTranslationBatchResult? result = null;
        await foreach (SubtitleTranslationBatchResult batch in translator.TranslateAsync(
                           Request(batchSize: 2),
                           CancellationToken.None))
        {
            result = batch;
        }

        Assert.NotNull(result);
        Assert.Equal(2, requestCount);
        Assert.Equal(2, result.RequestCount);
    }

    [Fact]
    public async Task TranslateAsyncRetriesTimeoutAndKeepsLastCompletedCheckpoint()
    {
        int requestCount = 0;
        var handler = new StubHandler((_, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                return Task.FromResult(Response(
                    """{"translations":[{"sequence":7,"translatedText":"第一"}]}""",
                    10,
                    5));
            }

            throw new TaskCanceledException("stub timeout");
        });
        DeepSeekSubtitleTranslator translator = Create(handler);
        var completed = new List<SubtitleTranslationBatchResult>();

        SubtitleTranslationException exception = await Assert.ThrowsAsync<SubtitleTranslationException>(async () =>
        {
            await foreach (SubtitleTranslationBatchResult batch in translator.TranslateAsync(
                               Request(batchSize: 1),
                               CancellationToken.None))
            {
                completed.Add(batch);
            }
        });

        Assert.Equal(AppErrorCode.Timeout, exception.Error.Code);
        Assert.Equal(new SubtitleTranslationCheckpoint("operation-1", 1), exception.ResumeFrom);
        Assert.Equal(new SubtitleTranslationCheckpoint("operation-1", 1), Assert.Single(completed).ResumeFrom);
        Assert.Equal(4, requestCount);
    }

    [Fact]
    public async Task TranslateAsyncResumesFromExactInputIndex()
    {
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain("first", body, StringComparison.Ordinal);
            Assert.Contains("second", body, StringComparison.Ordinal);
            return Response(
                """{"translations":[{"sequence":9,"translatedText":"第二"}]}""",
                10,
                5);
        });
        DeepSeekSubtitleTranslator translator = Create(handler);
        SubtitleTranslationRequest request = Request(
            batchSize: 1,
            new SubtitleTranslationCheckpoint("operation-1", 1));
        var results = new List<SubtitleTranslationBatchResult>();

        await foreach (SubtitleTranslationBatchResult result in translator.TranslateAsync(
                           request,
                           CancellationToken.None))
        {
            results.Add(result);
        }

        Assert.Equal(new SubtitleTranslationCheckpoint("operation-1", 2), Assert.Single(results).ResumeFrom);
    }

    [Fact]
    public async Task TranslateAsyncCancellationReturnsStructuredResumePosition()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        DeepSeekSubtitleTranslator translator = Create(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        SubtitleTranslationException exception = await Assert.ThrowsAsync<SubtitleTranslationException>(async () =>
        {
            await foreach (SubtitleTranslationBatchResult _ in translator.TranslateAsync(
                               Request(batchSize: 1),
                               cancellation.Token)) { }
        });

        Assert.Equal(AppErrorCode.OperationCancelled, exception.Error.Code);
        Assert.Equal(new SubtitleTranslationCheckpoint("operation-1", 0), exception.ResumeFrom);
    }

    private static DeepSeekSubtitleTranslator Create(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), new StubSecretStore());

    private static SubtitleTranslationRequest Request(
        int batchSize,
        SubtitleTranslationCheckpoint? checkpoint = null) =>
        SubtitleTranslationRequest.Create(
            "operation-1",
            "media-job-1",
            "简体中文",
            "deepseek-v4-flash",
            batchSize,
            [
                new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "first") { Sequence = 7 },
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "second") { Sequence = 9 }
            ],
            checkpoint);

    private static HttpResponseMessage Response(string content, int promptTokens, int completionTokens)
    {
        string json = JsonSerializer.Serialize(new
        {
            model = "deepseek-v4-flash",
            choices = new[] { new { message = new { content } } },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens
            }
        });
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request, cancellationToken);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private sealed class StubSecretStore : LenxTool.Core.Contracts.ISecretStore
    {
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-deepseek-key");

        public Task SetAsync(string name, string value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
