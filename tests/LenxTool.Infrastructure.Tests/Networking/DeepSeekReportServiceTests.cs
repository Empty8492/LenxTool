using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class DeepSeekReportServiceTests
{
    [Fact]
    public async Task GenerateArticleInsightUsesCurrentModelAndParsesUsage()
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-deepseek-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("https://api.deepseek.com/chat/completions", request.RequestUri?.AbsoluteUri);
            using JsonDocument requestBody = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(CancellationToken.None));
            Assert.Equal("deepseek-v4-flash", requestBody.RootElement.GetProperty("model").GetString());
            Assert.Equal(
                "disabled",
                requestBody.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.InRange(requestBody.RootElement.GetProperty("max_tokens").GetInt32(), 1, 1200);

            return JsonResponse(HttpStatusCode.OK, """
                {
                  "id":"chat-1",
                  "model":"deepseek-v4-flash",
                  "choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"核心判断\n本地 AI 正在加速。"}}],
                  "usage":{"prompt_tokens":120,"completion_tokens":30,"total_tokens":150}
                }
                """);
        });
        DeepSeekReportService service = CreateService(handler, "test-deepseek-key");
        NewsArticle article = new(
            "news-1", new DateOnly(2026, 7, 20), "AI 早报", "本地 AI",
            "摘要", "正文", "https://example.test/news", "hash", DateTimeOffset.UtcNow);

        AiReport report = await service.GenerateArticleInsightAsync(article, CancellationToken.None);

        Assert.Equal("news", report.EntityType);
        Assert.Equal(article.Id, report.EntityId);
        Assert.Equal("article_insight", report.ReportType);
        Assert.Equal("deepseek-v4-flash", report.Model);
        Assert.Equal(150, report.TokenUsage);
        Assert.Contains("本地 AI", report.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateTrendReportRejectsMissingKeyBeforeSending()
    {
        var handler = new StubHandler(_ =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("不应发送请求")));
        DeepSeekReportService service = CreateService(handler, null);
        TrendItem trend = new(
            "trend-1", "GitHub", 1, "AI 工具", "4.2k stars",
            "https://example.test/trend", "hash", DateTimeOffset.UtcNow);

        AppException exception = await Assert.ThrowsAsync<AppException>(() =>
            service.GenerateDailyTrendReportAsync([trend], CancellationToken.None));

        Assert.Equal(AppErrorCode.CredentialsInvalid, exception.Error.Code);
        Assert.Equal("DeepSeek", exception.Error.Provider);
    }

    private static DeepSeekReportService CreateService(HttpMessageHandler handler, string? apiKey) =>
        new(new StubHttpClientFactory(handler), new StubSecretStore(apiKey));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            : this(request => Task.FromResult(responseFactory(request))) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private sealed class StubSecretStore(string? apiKey) : ISecretStore
    {
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(apiKey);

        public Task SetAsync(string name, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
