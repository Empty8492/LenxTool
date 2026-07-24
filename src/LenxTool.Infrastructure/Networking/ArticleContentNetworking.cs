using System.Net;
using System.Net.Http.Headers;

namespace LenxTool.Infrastructure.Networking;

internal interface IArticleContentTransport
{
    Task<ArticleContentHttpResponse> SendAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken);
}

internal sealed class ArticleContentHttpResponse(
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

internal sealed class PinnedArticleContentTransport(
    FeedDiscoveryOptions feedOptions) : IArticleContentTransport
{
    public async Task<ArticleContentHttpResponse> SendAsync(
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
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LenxTool", "0.1"));
            request.Headers.Accept.ParseAdd(
                "text/html, application/xhtml+xml;q=0.9");
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
