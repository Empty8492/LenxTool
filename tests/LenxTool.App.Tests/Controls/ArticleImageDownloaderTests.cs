using System.Net;
using System.Net.Http.Headers;
using LenxTool.App.Controls;

namespace LenxTool.App.Tests.Controls;

public sealed class ArticleImageDownloaderTests
{
    [Fact]
    public async Task DownloadAsyncUsesBrowserCompatibleHeadersAndReturnsImageBytes()
    {
        HttpRequestMessage? captured = null;
        byte[] expected = [0x89, 0x50, 0x4E, 0x47];
        using var client = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expected)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        }));
        var downloader = new ArticleImageDownloader(client, 1024);

        byte[] actual = await downloader.DownloadAsync(
            "https://cdn.example/benchmark.png",
            "https://daily.example/posts/42",
            CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.NotNull(captured);
        Assert.Contains(captured.Headers.UserAgent, value => value.Product?.Name == "LenxTool");
        Assert.Equal("https://daily.example/posts/42", captured.Headers.Referrer?.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadAsyncRejectsPayloadLargerThanConfiguredLimit()
    {
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[5])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        }));
        var downloader = new ArticleImageDownloader(client, 4);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            "https://cdn.example/oversized.png",
            null,
            CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
