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

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
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
