using LenxTool.Core.Accounts;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Tests.Networking;

internal sealed class SignedOutWorkerAiProxyClient : IWorkerAiProxyClient
{
    public bool IsConfigured => false;

    public AccountSessionSnapshot Current => AccountSessionSnapshot.SignedOut;

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<HttpResponseMessage> SendSharedAiAsync(
        object payload,
        CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(
            new InvalidOperationException("The shared proxy is not configured for this test."));

    public void RecordSuccessfulSharedAiRequest()
    {
    }
}
