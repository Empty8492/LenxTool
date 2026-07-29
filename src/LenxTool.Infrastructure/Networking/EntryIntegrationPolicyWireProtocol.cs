using System.Text.Json;
using LenxTool.Core.Errors;
using LenxTool.Core.Exports;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// 集中约束 Worker 集成策略协议，避免宽松反序列化把未知类型或主机带入客户端。
/// </summary>
internal static class EntryIntegrationPolicyWireProtocol
{
    internal const long MaximumSafeInteger =
        9_007_199_254_740_991;
    private const int MaximumResponseBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal static object ToPayload(
        IReadOnlyList<EntryIntegrationPolicyInput> inputs)
    {
        IReadOnlyList<EntryIntegrationPolicy> policies =
            EntryIntegrationPolicyValidator
                .ValidateAndNormalizeSet(inputs);
        return new
        {
            Policies = policies.Select(policy => new
            {
                Kind = ToWireValue(policy.Kind),
                policy.IsEnabled,
                policy.AllowedHosts
            }).ToArray()
        };
    }

    internal static EntryIntegrationPolicySnapshot MapSnapshot(
        SnapshotDto dto,
        EntryIntegrationPolicyScope expectedScope)
    {
        if (dto.PolicySetVersion is < 0 or > MaximumSafeInteger
            || dto.GeneratedAt is null
            || dto.GeneratedAt.Value.Offset != TimeSpan.Zero
            || !string.Equals(
                dto.Scope,
                ToWireValue(expectedScope),
                StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }
        IReadOnlyList<EntryIntegrationPolicy> policies =
            MapPolicies(dto.Policies);
        if (expectedScope == EntryIntegrationPolicyScope.Active
            && policies.Any(policy => !policy.IsEnabled))
        {
            throw InvalidResponse();
        }
        return new(
            dto.PolicySetVersion,
            policies,
            expectedScope,
            dto.GeneratedAt);
    }

    internal static EntryIntegrationPolicyMutationResult MapMutation(
        MutationDto dto,
        long expectedVersion)
    {
        ValidateVersion(expectedVersion);
        if (dto.PolicySetVersion != expectedVersion + 1
            || dto.PolicySetVersion > MaximumSafeInteger)
        {
            throw InvalidResponse();
        }
        return new(
            dto.PolicySetVersion,
            MapPolicies(dto.Policies),
            IsReplay: false);
    }

    internal static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength
                > MaximumResponseBytes)
        {
            throw InvalidResponse();
        }
        await using Stream input =
            await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int read = await input.ReadAsync(
                buffer,
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumResponseBytes)
            {
                throw InvalidResponse();
            }
            output.Write(buffer, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(
                    output.GetBuffer().AsSpan(0, total),
                    JsonOptions)
                ?? throw InvalidResponse();
        }
        catch (JsonException exception)
        {
            throw new AppException(
                InvalidResponse().Error,
                exception);
        }
    }

    internal static void ValidateVersion(long value)
    {
        if (value is < 0 or > MaximumSafeInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    internal static AppException InvalidResponse() => new(new(
        AppErrorCode.ProviderUnavailable,
        "集成策略响应无效",
        "云服务没有返回可安全应用的集成类型与精确主机策略。",
        "不会启用任何未知目标；请刷新策略后重试。",
        Provider: "LenxTool Worker",
        IsRetryable: true));

    private static IReadOnlyList<EntryIntegrationPolicy> MapPolicies(
        List<PolicyDto?>? values)
    {
        if (values is null
            || values.Count
                > Enum.GetValues<EntryIntegrationKind>().Length)
        {
            throw InvalidResponse();
        }
        try
        {
            return EntryIntegrationPolicyValidator
                .ValidateAndNormalizeSet(
                    values.Select(value =>
                    {
                        if (value?.AllowedHosts is null)
                        {
                            throw InvalidResponse();
                        }
                        return new EntryIntegrationPolicyInput(
                            FromWireValue(value.Kind),
                            value.IsEnabled,
                            value.AllowedHosts);
                    }));
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException)
        {
            throw new AppException(
                InvalidResponse().Error,
                exception);
        }
    }

    private static string ToWireValue(
        EntryIntegrationPolicyScope scope) =>
        scope switch
        {
            EntryIntegrationPolicyScope.Active => "ACTIVE",
            EntryIntegrationPolicyScope.All => "ALL",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };

    private static string ToWireValue(EntryIntegrationKind kind) =>
        kind switch
        {
            EntryIntegrationKind.Obsidian => "OBSIDIAN",
            EntryIntegrationKind.Eagle => "EAGLE",
            EntryIntegrationKind.Zotero => "ZOTERO",
            EntryIntegrationKind.Readwise => "READWISE",
            EntryIntegrationKind.Cubox => "CUBOX",
            EntryIntegrationKind.Readeck => "READECK",
            EntryIntegrationKind.Outline => "OUTLINE",
            EntryIntegrationKind.QBittorrent => "QBITTORRENT",
            EntryIntegrationKind.Webhook => "WEBHOOK",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static EntryIntegrationKind FromWireValue(string? value) =>
        value switch
        {
            "OBSIDIAN" => EntryIntegrationKind.Obsidian,
            "EAGLE" => EntryIntegrationKind.Eagle,
            "ZOTERO" => EntryIntegrationKind.Zotero,
            "READWISE" => EntryIntegrationKind.Readwise,
            "CUBOX" => EntryIntegrationKind.Cubox,
            "READECK" => EntryIntegrationKind.Readeck,
            "OUTLINE" => EntryIntegrationKind.Outline,
            "QBITTORRENT" => EntryIntegrationKind.QBittorrent,
            "WEBHOOK" => EntryIntegrationKind.Webhook,
            _ => throw InvalidResponse()
        };

    internal sealed class SnapshotDto
    {
        public long PolicySetVersion { get; init; }
        public string? Scope { get; init; }
        public DateTimeOffset? GeneratedAt { get; init; }
        public List<PolicyDto?>? Policies { get; init; }
    }

    internal sealed class MutationDto
    {
        public long PolicySetVersion { get; init; }
        public List<PolicyDto?>? Policies { get; init; }
    }

    internal sealed class PolicyDto
    {
        public string? Kind { get; init; }
        public bool IsEnabled { get; init; }
        public List<string>? AllowedHosts { get; init; }
    }
}
