using System.Net;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class GroqWhisperClientTests : IDisposable
{
    private readonly string _audioPath = Path.Combine(Path.GetTempPath(), $"Lenx 音频 {Guid.NewGuid():N}.wav");

    [Fact]
    public async Task TranscribeParsesSegmentsAndUsesRequestScopedAuthorization()
    {
        await File.WriteAllBytesAsync(_audioPath, [1, 2, 3, 4], CancellationToken.None);
        var handler = new StubHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-groq-key", request.Headers.Authorization?.Parameter);
            return JsonResponse(HttpStatusCode.OK, """
                {"segments":[
                  {"start":0.25,"end":2.5,"text":" Hello world ","avg_logprob":-0.2,"no_speech_prob":0.01},
                  {"start":2.5,"end":3.0,"text":" noise ","avg_logprob":-1.5,"no_speech_prob":0.92}
                ]}
                """);
        });
        GroqWhisperClient client = CreateClient(handler);

        IReadOnlyList<SubtitleSegment> segments = await client.TranscribeAsync(
            _audioPath,
            "whisper-large-v3-turbo",
            null,
            CancellationToken.None);

        SubtitleSegment segment = Assert.Single(segments);
        Assert.Equal("Hello world", segment.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(250), segment.Start);
    }

    [Fact]
    public async Task RateLimitMapsRetryAfterAndGroqQuotaHeaders()
    {
        await File.WriteAllBytesAsync(_audioPath, [1], CancellationToken.None);
        var handler = new StubHandler(_ =>
        {
            HttpResponseMessage response = JsonResponse(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"rate limit\"}}");
            response.Headers.TryAddWithoutValidation("Retry-After", "42");
            response.Headers.TryAddWithoutValidation("x-ratelimit-limit-requests", "1000");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", "12");
            response.Headers.TryAddWithoutValidation("x-request-id", "req-groq-429");
            return response;
        });
        GroqWhisperClient client = CreateClient(handler);

        AppException exception = await Assert.ThrowsAsync<AppException>(() => client.TranscribeAsync(
            _audioPath,
            "whisper-large-v3-turbo",
            null,
            CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderRateLimited, exception.Error.Code);
        Assert.Equal(TimeSpan.FromSeconds(42), exception.Error.RetryAfter);
        Assert.Contains("1000", exception.Error.UserMessage, StringComparison.Ordinal);
        Assert.Contains("988", exception.Error.UserMessage, StringComparison.Ordinal);
        Assert.Equal("req-groq-429", exception.Error.RequestId);
    }

    public void Dispose()
    {
        if (File.Exists(_audioPath)) File.Delete(_audioPath);
    }

    private static GroqWhisperClient CreateClient(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), new StubSecretStore());

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    private sealed class StubSecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-groq-key");

        public Task SetAsync(string name, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
