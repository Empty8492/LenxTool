using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace LenxTool.Infrastructure.Networking;

internal interface IFeedHostResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal interface IFeedDiscoveryTransport
{
    Task<FeedDiscoveryHttpResponse> SendAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken);
}

internal sealed class FeedDiscoveryHttpResponse(
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

internal sealed class SystemFeedHostResolver : IFeedHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, cancellationToken)
            .ConfigureAwait(false);
}

internal sealed class PinnedFeedDiscoveryTransport(FeedDiscoveryOptions options) : IFeedDiscoveryTransport
{
    public async Task<FeedDiscoveryHttpResponse> SendAsync(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0) throw new ArgumentException("At least one pinned address is required.", nameof(addresses));
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
                if (!string.Equals(NormalizeHost(context.DnsEndPoint.Host), expectedHost, StringComparison.Ordinal)
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
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LenxTool", "0.1"));
            request.Headers.Accept.ParseAdd(
                "application/rss+xml, application/atom+xml, application/xml, text/xml, text/html, application/xhtml+xml");
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

    private static string NormalizeHost(string host) => host.TrimEnd('.').ToLowerInvariant();
}
