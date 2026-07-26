using System.Net;
using System.Net.Http.Headers;

namespace LenxTool.Infrastructure.Networking;

internal interface IFeedMediaTransport
{
    Task<FeedMediaHttpResponse> SendAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken);
}

internal sealed class FeedMediaHttpResponse(
    HttpResponseMessage message,
    IDisposable? owner = null) : IDisposable
{
    public HttpResponseMessage Message { get; } = message;

    public void Dispose()
    {
        Message.Dispose();
        owner?.Dispose();
    }
}

internal sealed class PinnedFeedMediaTransport(
    FeedDiscoveryOptions feedOptions) : IFeedMediaTransport
{
    public async Task<FeedMediaHttpResponse> SendAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken)
    {
        SocketsHttpHandler handler = PinnedHttpHandlerFactory.Create(
            uri,
            addresses,
            feedOptions.ConnectTimeout,
            DecompressionMethods.None);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(
                new ProductInfoHeaderValue("LenxTool", "0.1"));
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("audio/*"));
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("video/*"));
            HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return new(response, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
