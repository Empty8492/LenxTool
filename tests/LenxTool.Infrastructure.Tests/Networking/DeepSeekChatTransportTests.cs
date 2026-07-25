using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

public sealed class DeepSeekChatTransportTests
{
    [Fact]
    public async Task LocalKeyTakesPriorityAndDoesNotConsumeSharedQuota()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("https://api.deepseek.com/chat/completions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("local-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var worker = new StubWorkerAiProxyClient(UserSession(aiRemaining: 0));
        var transport = new DeepSeekChatTransport(
            new StubHttpClientFactory(handler),
            new StubSecretStore("local-key"),
            worker);

        using HttpResponseMessage response = await transport.SendAsync(
            new { model = "deepseek-v4-flash" },
            static () => { },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, worker.SendCalls);
        Assert.Equal(0, worker.RecordCalls);
    }

    [Fact]
    public async Task SignedInUserWithoutLocalKeyUsesSharedProxyAndRecordsSuccess()
    {
        var directHandler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Shared mode must not call DeepSeek directly.")));
        var worker = new StubWorkerAiProxyClient(UserSession(aiRemaining: 2))
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
        };
        var transport = new DeepSeekChatTransport(
            new StubHttpClientFactory(directHandler),
            new StubSecretStore(null),
            worker);

        using HttpResponseMessage response = await transport.SendAsync(
            new { model = "deepseek-v4-flash", stream = false },
            static () => { },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, directHandler.RequestCount);
        Assert.Equal(1, worker.SendCalls);
        Assert.Equal(1, worker.RecordCalls);
        using JsonDocument payload = JsonDocument.Parse(worker.LastPayload!);
        Assert.Equal("deepseek-v4-flash", payload.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ExhaustedSharedQuotaIsRejectedBeforeNetwork()
    {
        var worker = new StubWorkerAiProxyClient(UserSession(aiRemaining: 0));
        var transport = new DeepSeekChatTransport(
            new StubHttpClientFactory(new StubHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            new StubSecretStore(null),
            worker);

        AppException exception = await Assert.ThrowsAsync<AppException>(() =>
            transport.SendAsync(
                new { model = "deepseek-v4-flash" },
                static () => { },
                CancellationToken.None));

        Assert.Equal(AppErrorCode.ProviderRateLimited, exception.Error.Code);
        Assert.Contains("共享", exception.Error.UserMessage, StringComparison.Ordinal);
        Assert.Equal("limit=2; used=2; reserved=0; remaining=0", exception.Error.TechnicalDetails);
        Assert.Equal(0, worker.SendCalls);
    }

    [Fact]
    public async Task AdminBypassesLocalQuotaPrecheck()
    {
        var worker = new StubWorkerAiProxyClient(AdminSession())
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
        };
        var transport = new DeepSeekChatTransport(
            new StubHttpClientFactory(new StubHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            new StubSecretStore(null),
            worker);

        using HttpResponseMessage response = await transport.SendAsync(
            new { model = "deepseek-v4-flash" },
            static () => { },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, worker.SendCalls);
        Assert.Equal(0, worker.RecordCalls);
    }

    [Fact]
    public async Task MissingKeyAndSignedOutAccountExplainsBothOptions()
    {
        var worker = new StubWorkerAiProxyClient(AccountSessionSnapshot.SignedOut);
        var transport = new DeepSeekChatTransport(
            new StubHttpClientFactory(new StubHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            new StubSecretStore(null),
            worker);

        AppException exception = await Assert.ThrowsAsync<AppException>(() =>
            transport.SendAsync(
                new { model = "deepseek-v4-flash" },
                static () => { },
                CancellationToken.None));

        Assert.Equal(AppErrorCode.CredentialsInvalid, exception.Error.Code);
        Assert.Contains("DeepSeek Key", exception.Error.UserMessage, StringComparison.Ordinal);
        Assert.Contains("登录", exception.Error.Suggestion, StringComparison.Ordinal);
        Assert.Equal(0, worker.SendCalls);
    }

    private static AccountSessionSnapshot UserSession(int aiRemaining) =>
        new(
            AccountSessionStatus.SignedIn,
            new("user-1", "reader", AccountRole.User),
            new(
                DateOnly.FromDateTime(DateTime.UtcNow),
                new(2, 2 - aiRemaining, 0, aiRemaining),
                new(600, 0, 0, 600)));

    private static AccountSessionSnapshot AdminSession() =>
        new(
            AccountSessionStatus.SignedIn,
            new("admin-1", "admin", AccountRole.Admin),
            new(
                DateOnly.FromDateTime(DateTime.UtcNow),
                new(0, 0, 0, 0),
                new(0, 0, 0, 0)));

    private sealed class StubWorkerAiProxyClient(AccountSessionSnapshot current)
        : IWorkerAiProxyClient
    {
        public bool IsConfigured { get; init; } = true;

        public AccountSessionSnapshot Current { get; private set; } = current;

        public HttpResponseMessage Response { get; init; } =
            new(HttpStatusCode.ServiceUnavailable);

        public int SendCalls { get; private set; }

        public int RecordCalls { get; private set; }

        public string? LastPayload { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<HttpResponseMessage> SendSharedAiAsync(
            object payload,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            LastPayload = JsonSerializer.Serialize(payload);
            return await Task.FromResult(Response);
        }

        public void RecordSuccessfulSharedAiRequest()
        {
            RecordCalls++;
        }
    }

    private sealed class StubSecretStore(string? value) : ISecretStore
    {
        public Task SetAsync(string name, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(value);

        public Task DeleteAsync(string name, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return callback(request, cancellationToken);
        }
    }
}
