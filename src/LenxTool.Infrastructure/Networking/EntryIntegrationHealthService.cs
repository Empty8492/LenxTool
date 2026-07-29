using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// 在调用提供商专用探针前完成共享策略、凭据、SSRF、超时和限频检查。
/// P2-08 生产环境不注册探针，因此不会产生真实第三方网络请求。
/// </summary>
internal sealed class EntryIntegrationHealthService
    : IEntryIntegrationHealthService, IDisposable
{
    private readonly IEntryIntegrationPolicyService _policies;
    private readonly IEntryIntegrationCredentialStore _credentials;
    private readonly Dictionary<
        EntryIntegrationKind,
        IEntryIntegrationHealthProbe> _probes;
    private readonly IFeedHostResolver _resolver;
    private readonly EntryIntegrationHealthOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, DateTimeOffset>
        _lastAttempts = new(StringComparer.Ordinal);
    private bool _disposed;

    public EntryIntegrationHealthService(
        IEntryIntegrationPolicyService policies,
        IEntryIntegrationCredentialStore credentials,
        IEnumerable<IEntryIntegrationHealthProbe> probes,
        IFeedHostResolver resolver,
        EntryIntegrationHealthOptions options,
        TimeProvider timeProvider)
    {
        _policies = policies;
        _credentials = credentials;
        _resolver = resolver;
        _options = ValidateOptions(options);
        _timeProvider = timeProvider;
        _probes = BuildProbeMap(probes);
        _concurrency = new(_options.MaximumConcurrency);
    }

    public async Task<EntryIntegrationHealthResult> CheckAsync(
        EntryIntegrationTarget target,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        string targetKey = ValidateTargetId(target.TargetId);
        if (!Enum.IsDefined(target.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        EntryIntegrationPolicySnapshot snapshot =
            await _policies.GetAsync(
                EntryIntegrationPolicyScope.Active,
                cancellationToken).ConfigureAwait(false);
        EntryIntegrationPolicy? policy = snapshot.Policies
            .SingleOrDefault(item => item.Kind == target.Kind);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (policy is null || !policy.IsEnabled)
        {
            return Result(
                EntryIntegrationHealthStatus.PolicyDisabled,
                now);
        }
        if (!TryValidateEndpoint(
                target.Endpoint,
                policy.AllowedHosts,
                out Uri endpoint))
        {
            return Result(
                EntryIntegrationHealthStatus.BlockedEndpoint,
                now);
        }

        string? credential = await _credentials.GetAsync(
            target.Kind,
            target.TargetId,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential))
        {
            return Result(
                EntryIntegrationHealthStatus.CredentialsMissing,
                now);
        }
        if (!_probes.TryGetValue(
                target.Kind,
                out IEntryIntegrationHealthProbe? probe))
        {
            return Result(
                EntryIntegrationHealthStatus.AdapterUnavailable,
                now);
        }

        string cooldownKey = $"{(int)target.Kind}:{targetKey}";
        if (_lastAttempts.TryGetValue(
                cooldownKey,
                out DateTimeOffset lastAttempt))
        {
            TimeSpan remaining =
                _options.Cooldown - (now - lastAttempt);
            if (remaining > TimeSpan.Zero)
            {
                return Result(
                    EntryIntegrationHealthStatus.RateLimited,
                    now,
                    remaining);
            }
        }
        _lastAttempts[cooldownKey] = now;

        await _concurrency.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            IReadOnlyList<IPAddress> addresses;
            try
            {
                addresses = await ResolvePublicAsync(
                    endpoint,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Result(
                    EntryIntegrationHealthStatus.BlockedEndpoint,
                    _timeProvider.GetUtcNow());
            }

            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            using ITimer timer = _timeProvider.CreateTimer(
                _ => timeout.Cancel(),
                null,
                _options.Timeout,
                Timeout.InfiniteTimeSpan);
            try
            {
                EntryIntegrationProbeResult providerResult =
                    await probe.ProbeAsync(
                        new(
                            endpoint,
                            Array.AsReadOnly(addresses.ToArray())),
                        credential,
                        timeout.Token).ConfigureAwait(false);
                return NormalizeProbeResult(
                    providerResult,
                    _timeProvider.GetUtcNow());
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Result(
                    EntryIntegrationHealthStatus.TimedOut,
                    _timeProvider.GetUtcNow());
            }
            catch
            {
                return Result(
                    EntryIntegrationHealthStatus.Unavailable,
                    _timeProvider.GetUtcNow());
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _concurrency.Dispose();
        _disposed = true;
    }

    private async Task<IReadOnlyList<IPAddress>> ResolvePublicAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        string host = NormalizeHost(endpoint.IdnHost);
        IReadOnlyList<IPAddress> resolved;
        try
        {
            resolved = await _resolver.ResolveAsync(
                host,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is SocketException or ArgumentException)
        {
            throw new AppException(
                AppErrorFactory.FromNetwork("集成健康检查"),
                exception);
        }
        IPAddress[] addresses = resolved.Distinct().ToArray();
        if (addresses.Length == 0
            || addresses.Any(address =>
                NetworkTargetClassifier.Classify(address)
                    is NetworkAddressDisposition.Private
                    or NetworkAddressDisposition.Forbidden))
        {
            throw new InvalidOperationException(
                "集成目标解析到了不允许的网络地址。");
        }
        return Array.AsReadOnly(addresses);
    }

    private static bool TryValidateEndpoint(
        Uri? value,
        IReadOnlyList<string> allowedHosts,
        out Uri endpoint)
    {
        endpoint = null!;
        if (value is null
            || !value.IsAbsoluteUri
            || value.AbsoluteUri.Length > 2048
            || value.Scheme != Uri.UriSchemeHttps
            || value.Port != 443
            || !string.IsNullOrEmpty(value.UserInfo)
            || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment)
            || IPAddress.TryParse(value.IdnHost, out _))
        {
            return false;
        }
        string host;
        try
        {
            host = NormalizeHost(value.IdnHost);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (NetworkTargetClassifier.IsReservedHostName(host)
            || !allowedHosts.Contains(
                host,
                StringComparer.Ordinal))
        {
            return false;
        }
        endpoint = value;
        return true;
    }

    private static EntryIntegrationHealthResult NormalizeProbeResult(
        EntryIntegrationProbeResult? value,
        DateTimeOffset checkedAt)
    {
        if (value is null
            || value.Status is not (
                EntryIntegrationHealthStatus.Healthy
                or EntryIntegrationHealthStatus.BlockedEndpoint
                or EntryIntegrationHealthStatus.Unauthorized
                or EntryIntegrationHealthStatus.RateLimited
                or EntryIntegrationHealthStatus.Unavailable))
        {
            return Result(
                EntryIntegrationHealthStatus.Unavailable,
                checkedAt);
        }
        TimeSpan? retryAfter =
            value.Status == EntryIntegrationHealthStatus.RateLimited
                ? BoundRetryAfter(value.RetryAfter)
                : null;
        return Result(value.Status, checkedAt, retryAfter);
    }

    private static TimeSpan? BoundRetryAfter(TimeSpan? value)
    {
        if (value is null) return null;
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        return value > TimeSpan.FromHours(24)
            ? TimeSpan.FromHours(24)
            : value;
    }

    private static EntryIntegrationHealthResult Result(
        EntryIntegrationHealthStatus status,
        DateTimeOffset checkedAt,
        TimeSpan? retryAfter = null) =>
        new(status, checkedAt, retryAfter);

    private static string ValidateTargetId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value.Any(char.IsControl)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "集成目标标识无效。",
                nameof(value));
        }
        return value;
    }

    private static string NormalizeHost(string value)
    {
        string host = value.Trim().TrimEnd('.');
        return new IdnMapping().GetAscii(host).ToLowerInvariant();
    }

    private static EntryIntegrationHealthOptions ValidateOptions(
        EntryIntegrationHealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout < TimeSpan.FromSeconds(1)
            || options.Timeout > TimeSpan.FromSeconds(30)
            || options.Cooldown < TimeSpan.Zero
            || options.Cooldown > TimeSpan.FromMinutes(10)
            || options.MaximumConcurrency is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return options;
    }

    private static Dictionary<
        EntryIntegrationKind,
        IEntryIntegrationHealthProbe> BuildProbeMap(
            IEnumerable<IEntryIntegrationHealthProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        var result =
            new Dictionary<
                EntryIntegrationKind,
                IEntryIntegrationHealthProbe>();
        foreach (IEntryIntegrationHealthProbe probe in probes)
        {
            ArgumentNullException.ThrowIfNull(probe);
            if (!Enum.IsDefined(probe.Kind)
                || !result.TryAdd(probe.Kind, probe))
            {
                throw new ArgumentException(
                    "集成健康探针必须使用唯一且受支持的类型。",
                    nameof(probes));
            }
        }
        return result;
    }
}
