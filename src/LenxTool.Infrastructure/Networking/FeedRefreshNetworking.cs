using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace LenxTool.Infrastructure.Networking;

internal sealed record FeedRefreshHttpRequest(
    string? ETag,
    string? LastModified);

internal interface IFeedRefreshTransport
{
    Task<FeedRefreshHttpResponse> SendAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        FeedRefreshHttpRequest request,
        CancellationToken cancellationToken);
}

internal sealed class FeedRefreshHttpResponse(
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

internal sealed class PinnedFeedRefreshTransport(FeedDiscoveryOptions options) : IFeedRefreshTransport
{
    public async Task<FeedRefreshHttpResponse> SendAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        FeedRefreshHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(request);
        if (addresses.Count == 0)
            throw new ArgumentException("At least one pinned address is required.", nameof(addresses));

        IPAddress[] pinnedAddresses = addresses.ToArray();
        string expectedHost = NormalizeHost(uri.IdnHost);
        int expectedPort = uri.Port;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.ConnectTimeout,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                if (!string.Equals(
                        NormalizeHost(context.DnsEndPoint.Host),
                        expectedHost,
                        StringComparison.Ordinal)
                    || context.DnsEndPoint.Port != expectedPort)
                {
                    throw new HttpRequestException("The HTTP handler requested an unapproved endpoint.");
                }

                Exception? lastFailure = null;
                foreach (IPAddress address in pinnedAddresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, expectedPort), token)
                            .ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                    {
                        lastFailure = exception;
                        socket.Dispose();
                        if (exception is OperationCanceledException) throw;
                    }
                }

                throw new HttpRequestException("Unable to connect to an approved Feed endpoint.", lastFailure);
            }
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.UserAgent.Add(new ProductInfoHeaderValue("LenxTool", "0.1"));
            message.Headers.Accept.ParseAdd(
                "application/rss+xml, application/atom+xml, application/xml, text/xml");
            if (EntityTagHeaderValue.TryParse(request.ETag, out EntityTagHeaderValue? entityTag))
            {
                message.Headers.IfNoneMatch.Add(entityTag);
            }
            if (DateTimeOffset.TryParse(
                    request.LastModified,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset modified))
            {
                message.Headers.IfModifiedSince = modified;
            }

            HttpResponseMessage response = await client.SendAsync(
                message,
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

    private static string NormalizeHost(string host) => host.TrimEnd('.').ToLowerInvariant();
}
