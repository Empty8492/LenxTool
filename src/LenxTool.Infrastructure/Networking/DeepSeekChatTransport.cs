using System.Net.Http.Headers;
using System.Net.Http.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;

namespace LenxTool.Infrastructure.Networking;

public interface IDeepSeekChatTransport
{
    Task<HttpResponseMessage> SendAsync(
        object payload,
        Action requestStarted,
        CancellationToken cancellationToken);
}

public interface IWorkerAiProxyClient
{
    bool IsConfigured { get; }

    AccountSessionSnapshot Current { get; }

    Task RefreshAsync(CancellationToken cancellationToken);

    Task<HttpResponseMessage> SendSharedAiAsync(
        object payload,
        CancellationToken cancellationToken);

    void RecordSuccessfulSharedAiRequest();
}

public sealed class DeepSeekChatTransport(
    IHttpClientFactory httpClientFactory,
    ISecretStore secretStore,
    IWorkerAiProxyClient workerClient) : IDeepSeekChatTransport
{
    private static readonly Uri DeepSeekEndpoint =
        new("https://api.deepseek.com/chat/completions");

    public async Task<HttpResponseMessage> SendAsync(
        object payload,
        Action requestStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(requestStarted);

        string? apiKey = await secretStore.GetAsync("deepseek_api_key", cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            using HttpRequestMessage request = new(HttpMethod.Post, DeepSeekEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(payload);
            using HttpClient client = httpClientFactory.CreateClient("LenxTool.DeepSeek");
            requestStarted();
            return await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }

        AccountSessionSnapshot session = await GetCurrentWorkerSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!session.IsAuthenticated)
        {
            throw new AppException(new(
                AppErrorCode.CredentialsInvalid,
                "尚未配置 AI 凭据",
                "Feed AI 需要 DeepSeek Key，或使用已登录账号的共享额度。",
                "请在设置中保存 DeepSeek Key，或登录可用的 LenxTool 账号。",
                Provider: "DeepSeek"));
        }

        if (!session.IsAdmin)
        {
            AccountQuotaCounter quota = session.Quota!.Ai;
            if (quota.Remaining <= 0)
            {
                throw new AppException(new(
                    AppErrorCode.ProviderRateLimited,
                    "今日共享 AI 额度已用完",
                    $"今日共享 AI 额度已用完（{quota.Used + quota.Reserved}/{quota.Limit}）。",
                    "请明日再试，联系管理员调整额度，或在设置中改用自备 DeepSeek Key。",
                    $"limit={quota.Limit}; used={quota.Used}; reserved={quota.Reserved}; remaining={quota.Remaining}",
                    "LenxTool Worker",
                    RetryAfter: TimeSpan.FromDays(1),
                    IsRetryable: true));
            }
        }

        requestStarted();
        HttpResponseMessage response = await workerClient.SendSharedAiAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && !session.IsAdmin)
            workerClient.RecordSuccessfulSharedAiRequest();
        return response;
    }

    private async Task<AccountSessionSnapshot> GetCurrentWorkerSessionAsync(
        CancellationToken cancellationToken)
    {
        if (!workerClient.IsConfigured) return AccountSessionSnapshot.SignedOut;

        AccountSessionSnapshot session = workerClient.Current;
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (session.IsAuthenticated
            && !session.IsAdmin
            && (session.Quota is null || session.Quota.Date != today))
        {
            await workerClient.RefreshAsync(cancellationToken).ConfigureAwait(false);
            session = workerClient.Current;
        }

        return session;
    }
}
