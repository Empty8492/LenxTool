using System.Net;
using System.Net.Sockets;

namespace LenxTool.Infrastructure.Networking;

internal static class PinnedHttpHandlerFactory
{
    public static SocketsHttpHandler Create(
        Uri uri,
        IReadOnlyList<IPAddress> addresses,
        TimeSpan connectTimeout,
        DecompressionMethods automaticDecompression)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            throw new ArgumentException(
                "At least one pinned address is required.",
                nameof(addresses));
        }

        IPAddress[] pinnedAddresses = addresses.ToArray();
        string expectedHost = NormalizeHost(uri.IdnHost);
        int expectedPort = uri.Port;
        return new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = automaticDecompression,
            ConnectTimeout = connectTimeout,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!string.Equals(
                        NormalizeHost(context.DnsEndPoint.Host),
                        expectedHost,
                        StringComparison.Ordinal)
                    || context.DnsEndPoint.Port != expectedPort)
                {
                    throw new HttpRequestException(
                        "The HTTP handler requested an unapproved endpoint.");
                }

                Exception? lastFailure = null;
                foreach (IPAddress address in pinnedAddresses)
                {
                    var socket = new Socket(
                        address.AddressFamily,
                        SocketType.Stream,
                        ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        await socket.ConnectAsync(
                            new IPEndPoint(address, expectedPort),
                            cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (
                        exception is SocketException or OperationCanceledException)
                    {
                        lastFailure = exception;
                        socket.Dispose();
                        if (exception is OperationCanceledException)
                        {
                            throw;
                        }
                    }
                }

                throw new HttpRequestException(
                    "Unable to connect to an approved endpoint.",
                    lastFailure);
            }
        };
    }

    private static string NormalizeHost(string host) =>
        host.TrimEnd('.').ToLowerInvariant();
}
