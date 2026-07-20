using System.Net;
using LenxTool.Core.Errors;

namespace LenxTool.Core.Tests.Errors;

public sealed class AppErrorFactoryTests
{
    [Fact]
    public void FromHttpMapsUnauthorizedToCredentialsError()
    {
        AppError error = AppErrorFactory.FromHttp(
            HttpStatusCode.Unauthorized,
            provider: "Groq",
            requestId: "req-401");

        Assert.Equal(AppErrorCode.CredentialsInvalid, error.Code);
        Assert.Equal("认证失败", error.Title);
        Assert.False(error.IsRetryable);
        Assert.Equal("req-401", error.RequestId);
    }

    [Fact]
    public void FromHttpMapsRateLimitWithQuotaDetailsAndRetryAfter()
    {
        TimeSpan retryAfter = TimeSpan.FromSeconds(45);

        AppError error = AppErrorFactory.FromHttp(
            HttpStatusCode.TooManyRequests,
            provider: "Groq",
            requestId: "req-429",
            retryAfter: retryAfter,
            limit: "600 秒/天",
            used: "598 秒");

        Assert.Equal(AppErrorCode.ProviderRateLimited, error.Code);
        Assert.True(error.IsRetryable);
        Assert.Equal(retryAfter, error.RetryAfter);
        Assert.Contains("600 秒/天", error.UserMessage, StringComparison.Ordinal);
        Assert.Contains("598 秒", error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeoutAndNetworkFailuresHaveDifferentGuidance()
    {
        AppError timeout = AppErrorFactory.FromTimeout("DeepSeek", "req-timeout");
        AppError network = AppErrorFactory.FromNetwork("DeepSeek", "req-network");

        Assert.Equal(AppErrorCode.Timeout, timeout.Code);
        Assert.Equal(AppErrorCode.NetworkUnavailable, network.Code);
        Assert.NotEqual(timeout.UserMessage, network.UserMessage);
        Assert.True(timeout.IsRetryable);
        Assert.True(network.IsRetryable);
    }
}
