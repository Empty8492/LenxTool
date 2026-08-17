using System.Reflection;
using LenxTool.Core.Exports;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Exports;

/// <summary>
/// 冻结本机集成零主机、网络集成精确主机、不可变集合和无凭据公共模型边界。
/// </summary>
public sealed class EntryIntegrationPolicyTests
{
    [Fact]
    public void ValidatorNormalizesExactHostsAndFreezesInput()
    {
        var hosts = new List<string>
        {
            "API.Readwise.IO.",
            "reader.example.com"
        };
        var input = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.Readwise,
            IsEnabled: true,
            hosts);

        EntryIntegrationPolicy normalized =
            EntryIntegrationPolicyValidator.ValidateAndNormalize(input);
        hosts[0] = "changed.example";

        Assert.Equal(
            ["api.readwise.io", "reader.example.com"],
            normalized.AllowedHosts);
        Assert.True(normalized.IsEnabled);
    }

    [Theory]
    [InlineData("*.example.com")]
    [InlineData("https://example.com")]
    [InlineData("user@example.com")]
    [InlineData("example.com:443")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.8")]
    [InlineData("service.local")]
    [InlineData("service.home.arpa")]
    public void ValidatorRejectsAmbiguousOrPrivateHostSyntax(string host)
    {
        var input = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.Webhook,
            IsEnabled: true,
            [host]);

        Assert.Throws<ArgumentException>(
            () => EntryIntegrationPolicyValidator.ValidateAndNormalize(input));
    }

    [Fact]
    public void ValidatorAllowsHostlessLocalPoliciesAndRequiresHostsForNetworkKinds()
    {
        EntryIntegrationPolicy obsidian =
            EntryIntegrationPolicyValidator.ValidateAndNormalize(
                new(
                    EntryIntegrationKind.Obsidian,
                    IsEnabled: true,
                    []));
        EntryIntegrationPolicy eagle =
            EntryIntegrationPolicyValidator.ValidateAndNormalize(
                new(
                    EntryIntegrationKind.Eagle,
                    IsEnabled: true,
                    []));
        EntryIntegrationPolicy disabledNetwork =
            EntryIntegrationPolicyValidator.ValidateAndNormalize(
                new(
                    EntryIntegrationKind.Webhook,
                    IsEnabled: false,
                    []));

        Assert.True(obsidian.IsEnabled);
        Assert.Empty(obsidian.AllowedHosts);
        Assert.True(eagle.IsEnabled);
        Assert.Empty(eagle.AllowedHosts);
        Assert.False(disabledNetwork.IsEnabled);
        Assert.Empty(disabledNetwork.AllowedHosts);
        EntryIntegrationKind[] networkKinds =
        [
            EntryIntegrationKind.Zotero,
            EntryIntegrationKind.Readwise,
            EntryIntegrationKind.Cubox,
            EntryIntegrationKind.Readeck,
            EntryIntegrationKind.Outline,
            EntryIntegrationKind.QBittorrent,
            EntryIntegrationKind.Webhook
        ];
        Assert.All(
            networkKinds,
            kind => Assert.Throws<ArgumentException>(
                () => EntryIntegrationPolicyValidator.ValidateAndNormalize(
                    new(kind, IsEnabled: true, []))));
    }

    [Fact]
    public void ValidatorNormalizesPrivateEndpointsResourcesAndLoopbackPorts()
    {
        var privateEndpoints = new List<EntryIntegrationPrivateEndpoint>
        {
            new("QBIT.HOME.ARPA.", 8443),
            new("qbit.home.arpa", 8443)
        };
        var resources = new List<string> { " downloads ", "downloads" };
        var loopbackPorts = new List<int> { 8080, 8080 };
        var input = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.QBittorrent,
            IsEnabled: true,
            [])
        {
            TrustedPrivateEndpoints = privateEndpoints,
            AllowedResources = resources,
            AllowedLoopbackHttpPorts = loopbackPorts
        };

        EntryIntegrationPolicy normalized =
            EntryIntegrationPolicyValidator.ValidateAndNormalize(input);
        privateEndpoints[0] = new("changed.example", 443);
        resources[0] = "changed";
        loopbackPorts[0] = 9999;

        Assert.Equal(
            [new EntryIntegrationPrivateEndpoint("qbit.home.arpa", 8443)],
            normalized.TrustedPrivateEndpoints);
        Assert.Equal(["downloads"], normalized.AllowedResources);
        Assert.Equal([8080], normalized.AllowedLoopbackHttpPorts);
    }

    [Theory]
    [InlineData("*.home.arpa", 443)]
    [InlineData("https://qbit.home.arpa", 443)]
    [InlineData("127.0.0.1", 443)]
    [InlineData("0177.0.0.1", 443)]
    [InlineData("service.local", 443)]
    [InlineData("qbit.home.arpa", 0)]
    [InlineData("qbit.home.arpa", 65536)]
    public void ValidatorRejectsUnsafePrivateEndpointSyntax(
        string host,
        int port)
    {
        var input = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.QBittorrent,
            IsEnabled: false,
            [])
        {
            TrustedPrivateEndpoints =
                [new EntryIntegrationPrivateEndpoint(host, port)]
        };

        Assert.ThrowsAny<ArgumentException>(
            () => EntryIntegrationPolicyValidator.ValidateAndNormalize(input));
    }

    [Fact]
    public void ValidatorRestrictsExtendedMetadataToApprovedKinds()
    {
        var readwisePrivate = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.Readwise,
            IsEnabled: false,
            ["api.readwise.io"])
        {
            TrustedPrivateEndpoints =
                [new EntryIntegrationPrivateEndpoint("reader.home.arpa", 443)]
        };
        var webhookResources = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.Webhook,
            IsEnabled: false,
            ["hooks.example.com"])
        {
            AllowedResources = ["unexpected"]
        };
        var outlineLoopback = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.Outline,
            IsEnabled: false,
            ["outline.example.com"])
        {
            AllowedLoopbackHttpPorts = [3000]
        };

        Assert.Throws<ArgumentException>(
            () => EntryIntegrationPolicyValidator
                .ValidateAndNormalize(readwisePrivate));
        Assert.Throws<ArgumentException>(
            () => EntryIntegrationPolicyValidator
                .ValidateAndNormalize(webhookResources));
        Assert.Throws<ArgumentException>(
            () => EntryIntegrationPolicyValidator
                .ValidateAndNormalize(outlineLoopback));
    }

    [Fact]
    public void EnabledOutlineAndQBittorrentRequireApprovedResources()
    {
        var outlineWithoutCollection = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.Outline,
            IsEnabled: true,
            ["outline.example.com"]);
        var qbitWithoutCategory = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.QBittorrent,
            IsEnabled: true,
            [])
        {
            AllowedLoopbackHttpPorts = [8080]
        };

        Assert.Throws<ArgumentException>(
            () => EntryIntegrationPolicyValidator
                .ValidateAndNormalize(outlineWithoutCollection));
        Assert.Throws<ArgumentException>(
            () => EntryIntegrationPolicyValidator
                .ValidateAndNormalize(qbitWithoutCategory));
    }

    [Fact]
    public void EnabledPrivateAndLoopbackTargetsSatisfyNetworkTargetRequirement()
    {
        EntryIntegrationPolicy readeck =
            EntryIntegrationPolicyValidator.ValidateAndNormalize(
                new(
                    EntryIntegrationKind.Readeck,
                    IsEnabled: true,
                    [])
                {
                    TrustedPrivateEndpoints =
                    [
                        new EntryIntegrationPrivateEndpoint(
                            "readeck.home.arpa",
                            8443)
                    ]
                });
        EntryIntegrationPolicy qbit =
            EntryIntegrationPolicyValidator.ValidateAndNormalize(
                new(
                    EntryIntegrationKind.QBittorrent,
                    IsEnabled: true,
                    [])
                {
                    AllowedResources = ["downloads"],
                    AllowedLoopbackHttpPorts = [8080]
                });

        Assert.Empty(readeck.AllowedHosts);
        Assert.Single(readeck.TrustedPrivateEndpoints);
        Assert.Empty(qbit.AllowedHosts);
        Assert.Equal([8080], qbit.AllowedLoopbackHttpPorts);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void OutlineCollectionAllowlistRequiresCanonicalNonEmptyUuid(
        string collectionId)
    {
        var input = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.Outline,
            IsEnabled: false,
            ["outline.example.com"])
        {
            AllowedResources = [collectionId]
        };

        Assert.Throws<ArgumentException>(
            () => EntryIntegrationPolicyValidator.ValidateAndNormalize(input));
    }

    [Fact]
    public void ValidatorRejectsC1ControlsAndOversizedPrivateEndpointJson()
    {
        var control = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.QBittorrent,
            IsEnabled: false,
            [])
        {
            AllowedResources = ["down\u0085loads"]
        };
        EntryIntegrationPrivateEndpoint[] oversized = Enumerable
            .Range(0, 32)
            .Select(index => new EntryIntegrationPrivateEndpoint(
                CreateMaximumLengthHost(index),
                443))
            .ToArray();
        var privateEndpoints = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.Webhook,
            IsEnabled: false,
            [])
        {
            TrustedPrivateEndpoints = oversized
        };

        Assert.Throws<ArgumentException>(
            () => EntryIntegrationPolicyValidator.ValidateAndNormalize(
                control));
        Assert.ThrowsAny<ArgumentException>(
            () => EntryIntegrationPolicyValidator.ValidateAndNormalize(
                privateEndpoints));
    }

    [Fact]
    public void ValidatorPreservesUnicodeQBittorrentCategories()
    {
        const string category = "下载🚀\"\\分类";
        var input = new EntryIntegrationPolicyInput(
            EntryIntegrationKind.QBittorrent,
            IsEnabled: false,
            [])
        {
            AllowedResources = [category]
        };

        EntryIntegrationPolicy policy =
            EntryIntegrationPolicyValidator.ValidateAndNormalize(input);

        Assert.Equal([category], policy.AllowedResources);
    }

    private static string CreateMaximumLengthHost(int index) =>
        $"{index:D2}." +
        $"{new string('a', 63)}." +
        $"{new string('b', 63)}." +
        $"{new string('c', 63)}." +
        new string('d', 58);

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.000.0.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("0177.0.0.1")]
    [InlineData("eagle.example.com")]
    public void ValidatorRejectsEveryLocalIntegrationAllowedHost(string host)
    {
        // Vault 与 Eagle 端点都属于用户本机设置，任何主机值都不得进入共享策略。
        EntryIntegrationKind[] localKinds =
        [
            EntryIntegrationKind.Obsidian,
            EntryIntegrationKind.Eagle
        ];
        Assert.All(
            localKinds,
            kind => Assert.Throws<ArgumentException>(
                () => EntryIntegrationPolicyValidator.ValidateAndNormalize(
                    new(kind, IsEnabled: true, [host]))));
    }

    [Fact]
    public void PolicySetRejectsDuplicateAndUndefinedKinds()
    {
        var duplicate = new[]
        {
            new EntryIntegrationPolicyInput(
                EntryIntegrationKind.Zotero,
                true,
                ["api.zotero.org"]),
            new EntryIntegrationPolicyInput(
                EntryIntegrationKind.Zotero,
                false,
                [])
        };
        var undefined = new[]
        {
            new EntryIntegrationPolicyInput(
                (EntryIntegrationKind)999,
                true,
                ["example.com"])
        };

        Assert.Throws<ArgumentException>(
            () => EntryIntegrationPolicyValidator
                .ValidateAndNormalizeSet(duplicate));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EntryIntegrationPolicyValidator
                .ValidateAndNormalizeSet(undefined));
    }

    [Fact]
    public void EmptyPolicySetKeepsEveryExternalIntegrationDisabled()
    {
        IReadOnlyList<EntryIntegrationPolicy> policies =
            EntryIntegrationPolicyValidator.ValidateAndNormalizeSet([]);

        Assert.Empty(policies);
        Assert.All(
            Enum.GetValues<EntryIntegrationKind>(),
            kind => Assert.DoesNotContain(
                policies,
                policy => policy.Kind == kind && policy.IsEnabled));
    }

    [Fact]
    public void PublicPolicyModelsCannotCarryCredentialsOrProviderBodies()
    {
        Type[] policyTypes =
        [
            typeof(EntryIntegrationPolicyInput),
            typeof(EntryIntegrationPolicy),
            typeof(EntryIntegrationPolicySnapshot),
            typeof(EntryIntegrationPolicyMutationResult)
        ];
        string[] forbidden =
        [
            "token",
            "password",
            "credential",
            "secret",
            "authorization",
            "response"
        ];

        Assert.All(
            policyTypes.SelectMany(type => type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public)),
            property => Assert.DoesNotContain(
                forbidden,
                word => property.Name.Contains(
                    word,
                    StringComparison.OrdinalIgnoreCase)));
    }
}
