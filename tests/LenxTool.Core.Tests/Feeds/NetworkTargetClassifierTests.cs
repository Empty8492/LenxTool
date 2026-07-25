using System.Net;
using LenxTool.Core.Feeds;

namespace LenxTool.Core.Tests.Feeds;

public sealed class NetworkTargetClassifierTests
{
    [Theory]
    [InlineData("8.8.8.8", NetworkAddressDisposition.Public)]
    [InlineData("10.0.0.1", NetworkAddressDisposition.Private)]
    [InlineData("100.64.0.1", NetworkAddressDisposition.Private)]
    [InlineData("127.0.0.1", NetworkAddressDisposition.Forbidden)]
    [InlineData("169.254.1.1", NetworkAddressDisposition.Forbidden)]
    [InlineData("198.18.0.1", NetworkAddressDisposition.SyntheticProxy)]
    [InlineData("::1", NetworkAddressDisposition.Forbidden)]
    [InlineData("fc00::1", NetworkAddressDisposition.Private)]
    [InlineData("2001:db8::1", NetworkAddressDisposition.Forbidden)]
    [InlineData("2606:4700:4700::1111", NetworkAddressDisposition.Public)]
    public void ClassifiesLiteralAddresses(
        string value,
        NetworkAddressDisposition expected)
    {
        NetworkAddressDisposition actual =
            NetworkTargetClassifier.Classify(IPAddress.Parse(value));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("api.localhost")]
    [InlineData("printer.local")]
    [InlineData("service.internal")]
    [InlineData("home.arpa")]
    [InlineData("router.home.arpa")]
    public void RecognizesReservedHostNames(string host)
    {
        Assert.True(NetworkTargetClassifier.IsReservedHostName(host));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("local.example.com")]
    [InlineData("internal.example.com")]
    public void LeavesPublicHostNamesAvailable(string host)
    {
        Assert.False(NetworkTargetClassifier.IsReservedHostName(host));
    }
}
