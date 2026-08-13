using System.Net;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

internal interface IIntegrationHttpClientFactory
{
    HttpClient Create(EntryIntegrationProbeContext context);
}

/// <summary>
/// 外部集成写入统一复用禁代理、禁 Cookie、禁重定向和 DNS pin 的传输层。
/// </summary>
internal sealed class PinnedIntegrationHttpClientFactory
    : IIntegrationHttpClientFactory
{
    public HttpClient Create(EntryIntegrationProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SocketsHttpHandler handler = PinnedHttpHandlerFactory.Create(
            context.Endpoint,
            context.PinnedAddresses,
            TimeSpan.FromSeconds(5),
            DecompressionMethods.None);
        handler.MaxConnectionsPerServer = 1;
        return new(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
