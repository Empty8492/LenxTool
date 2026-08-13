using System.Collections.Concurrent;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// 在调用提供商专用探针前完成共享策略、凭据、SSRF、超时和限频检查。
/// </summary>
internal sealed class EntryIntegrationHealthService
    : IEntryIntegrationHealthService, IDisposable
{
    private readonly IEntryIntegrationPolicyService _policies;
    private readonly IEntryIntegrationCredentialStore _credentials;
    private readonly Dictionary<
        EntryIntegrationKind,
        IEntryIntegrationHealthProbe> _probes;
    private readonly EntryIntegrationEndpointAuthorizer _authorizer;
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
        _authorizer = new EntryIntegrationEndpointAuthorizer(resolver);
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
            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            using ITimer timer = _timeProvider.CreateTimer(
                _ => timeout.Cancel(),
                null,
                _options.Timeout,
                Timeout.InfiniteTimeSpan);
            EntryIntegrationProbeContext? context;
            try
            {
                context = await _authorizer.AuthorizeAsync(
                        target,
                        policy,
                        timeout.Token)
                    .ConfigureAwait(false);
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
                    EntryIntegrationHealthStatus.BlockedEndpoint,
                    _timeProvider.GetUtcNow());
            }
            if (context is null)
            {
                return Result(
                    EntryIntegrationHealthStatus.BlockedEndpoint,
                    _timeProvider.GetUtcNow());
            }

            // 先完成策略、端点与 DNS pin，再触碰 DPAPI 凭据；即使 DNS
            // 漂移到未批准地址，也不会把秘密解密进当前进程。
            string? credential;
            try
            {
                credential = probe.RequiresCredential
                    ? await _credentials.GetAsync(
                        target.Kind,
                        target.TargetId,
                        timeout.Token).ConfigureAwait(false)
                    : null;
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
            if (probe.RequiresCredential
                && string.IsNullOrWhiteSpace(credential))
            {
                return Result(
                    EntryIntegrationHealthStatus.CredentialsMissing,
                    _timeProvider.GetUtcNow());
            }

            try
            {
                EntryIntegrationProbeResult providerResult =
                    await probe.ProbeAsync(
                        context,
                        credential ?? string.Empty,
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
