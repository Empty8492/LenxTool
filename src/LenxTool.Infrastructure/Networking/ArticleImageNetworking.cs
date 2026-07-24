using System.Net;
using System.Net.Http.Headers;

namespace LenxTool.Infrastructure.Networking;

internal interface IArticleImageTransport
{
    Task<ArticleImageHttpResponse> SendAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        Uri? referrer,
        CancellationToken cancellationToken);
}

internal sealed class ArticleImageHttpResponse(
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

internal sealed class PinnedArticleImageTransport(
    FeedDiscoveryOptions feedOptions) : IArticleImageTransport
{
    public async Task<ArticleImageHttpResponse> SendAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        Uri? referrer,
        CancellationToken cancellationToken)
    {
        SocketsHttpHandler handler = PinnedHttpHandlerFactory.Create(
            uri,
            addresses,
            feedOptions.ConnectTimeout,
            DecompressionMethods.All);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LenxTool", "0.1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            request.Headers.Referrer = referrer;
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
