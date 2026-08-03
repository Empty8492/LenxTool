using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.App.Tests.ViewModels;

/// <summary>
/// 冻结 Eagle 本机设置的 RED 契约：只有显式 loopback HTTP 端点且
/// ACTIVE 策略允许时才能探测，设置保存本身不得绕过管理员门禁发起网络。
/// </summary>
public sealed class EagleSettingsViewModelTests
{
    private const string DefaultEndpoint =
        "http://127.0.0.1:41595/";

    [Fact]
    public void ConstructorUsesTheDocumentedLoopbackEndpoint()
    {
        var viewModel = new EagleSettingsViewModel(
            new FakeEagleExportTargetStore(),
            new FakeEntryIntegrationPolicyService(isEnabled: true),
            new FakeEagleApiClient());

        Assert.Equal(DefaultEndpoint, viewModel.EndpointText);
        Assert.NotNull(viewModel.SaveCommand);
        Assert.NotNull(viewModel.TestCommand);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.Status));
    }

    [Fact]
    public async Task SavePersistsDefaultTargetThenProbesAppAndLibraryCapabilities()
    {
        var store = new FakeEagleExportTargetStore();
        var policies = new FakeEntryIntegrationPolicyService(
            isEnabled: true);
        var api = new FakeEagleApiClient
        {
            Result = new(
                "4.0.0",
                21,
                "111111111111111111111111")
        };
        var viewModel = new EagleSettingsViewModel(
            store,
            policies,
            api);

        await viewModel.SaveCommand.ExecuteAsync();

        EagleExportTarget saved = Assert.IsType<EagleExportTarget>(
            store.Saved);
        Assert.Equal("default", saved.TargetId);
        Assert.Equal(DefaultEndpoint, saved.Endpoint.AbsoluteUri);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(
            EntryIntegrationPolicyScope.Active,
            Assert.Single(policies.Scopes));
        Assert.Equal(
            new Uri(DefaultEndpoint),
            Assert.Single(api.ProbedEndpoints));
        Assert.Contains("应用", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("资源库", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestCommandProbesCurrentEndpointWithoutSavingIt()
    {
        var store = new FakeEagleExportTargetStore();
        var api = new FakeEagleApiClient
        {
            Result = new(
                "4.0.0",
                21,
                "111111111111111111111111")
        };
        var viewModel = new EagleSettingsViewModel(
            store,
            new FakeEntryIntegrationPolicyService(isEnabled: true),
            api)
        {
            EndpointText = "http://127.0.0.1:41600/"
        };

        await viewModel.TestCommand.ExecuteAsync();

        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(
            new Uri("http://127.0.0.1:41600/"),
            Assert.Single(api.ProbedEndpoints));
        Assert.Contains("应用", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("资源库", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://127.0.0.1:41595/")]
    [InlineData("http://localhost:41595/")]
    [InlineData("http://192.0.2.10:41595/")]
    public async Task SaveRejectsNonExplicitLoopbackHttpWithoutSideEffects(
        string endpoint)
    {
        var store = new FakeEagleExportTargetStore();
        var policies = new FakeEntryIntegrationPolicyService(
            isEnabled: true);
        var api = new FakeEagleApiClient();
        var viewModel = new EagleSettingsViewModel(
            store,
            policies,
            api)
        {
            EndpointText = endpoint
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal(0, store.SaveCalls);
        Assert.Empty(policies.Scopes);
        Assert.Empty(api.ProbedEndpoints);
        Assert.Contains(
            "loopback HTTP",
            viewModel.Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisabledActivePolicyMaySaveLocallyButNeverCallsEagle()
    {
        var store = new FakeEagleExportTargetStore();
        var policies = new FakeEntryIntegrationPolicyService(
            isEnabled: false);
        var api = new FakeEagleApiClient();
        var viewModel = new EagleSettingsViewModel(
            store,
            policies,
            api);

        // 本机目标允许预先保存；管理员未启用时，自动探测和显式测试都必须零网络。
        await viewModel.SaveCommand.ExecuteAsync();
        await viewModel.TestCommand.ExecuteAsync();

        Assert.Equal(1, store.SaveCalls);
        Assert.Empty(api.ProbedEndpoints);
        Assert.Equal(
            [
                EntryIntegrationPolicyScope.Active,
                EntryIntegrationPolicyScope.Active
            ],
            policies.Scopes);
        Assert.Contains("管理员", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("启用", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyReadFailureAfterSaveNeverCallsEagleOrLeaksDetails(
        bool isInternalTimeout)
    {
        const string sensitiveDetail = "private-worker-response";
        var store = new FakeEagleExportTargetStore();
        var policies = new FakeEntryIntegrationPolicyService(
            isEnabled: true)
        {
            Failure = isInternalTimeout
                ? new OperationCanceledException(sensitiveDetail)
                : new HttpRequestException(sensitiveDetail)
        };
        var api = new FakeEagleApiClient();
        var viewModel = new EagleSettingsViewModel(
            store,
            policies,
            api);

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal(1, store.SaveCalls);
        Assert.Empty(api.ProbedEndpoints);
        Assert.Contains("策略", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain(
            sensitiveDetail,
            viewModel.Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveStorageTimeoutNeverClaimsTheEndpointWasSaved()
    {
        const string SensitiveDetail = "private-local-store-path";
        var store = new FakeEagleExportTargetStore
        {
            SaveFailure = new OperationCanceledException(SensitiveDetail)
        };
        var policies = new FakeEntryIntegrationPolicyService(
            isEnabled: true);
        var api = new FakeEagleApiClient();
        var viewModel = new EagleSettingsViewModel(
            store,
            policies,
            api);

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal(1, store.SaveCalls);
        Assert.Null(store.Saved);
        Assert.Empty(policies.Scopes);
        Assert.Empty(api.ProbedEndpoints);
        Assert.Contains("保存", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("已保存", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SensitiveDetail,
            viewModel.Status,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeTimeoutReportsAStartedConnectionWithoutLeakingDetails(
        bool saveFirst)
    {
        const string SensitiveDetail = "private-eagle-timeout";
        var store = new FakeEagleExportTargetStore();
        var api = new FakeEagleApiClient
        {
            Failure = new OperationCanceledException(SensitiveDetail)
        };
        var viewModel = new EagleSettingsViewModel(
            store,
            new FakeEntryIntegrationPolicyService(isEnabled: true),
            api);

        if (saveFirst)
        {
            await viewModel.SaveCommand.ExecuteAsync();
        }
        else
        {
            await viewModel.TestCommand.ExecuteAsync();
        }

        Assert.Equal(saveFirst ? 1 : 0, store.SaveCalls);
        Assert.Single(api.ProbedEndpoints);
        Assert.Contains("连接 Eagle", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("未发起", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SensitiveDetail,
            viewModel.Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestPolicyTimeoutNeverCallsEagleOrLeaksDetails()
    {
        const string SensitiveDetail = "private-policy-timeout";
        var api = new FakeEagleApiClient();
        var viewModel = new EagleSettingsViewModel(
            new FakeEagleExportTargetStore(),
            new FakeEntryIntegrationPolicyService(isEnabled: true)
            {
                Failure = new OperationCanceledException(SensitiveDetail)
            },
            api);

        await viewModel.TestCommand.ExecuteAsync();

        Assert.Empty(api.ProbedEndpoints);
        Assert.Contains("策略", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("未发起", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SensitiveDetail,
            viewModel.Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeCancelsActiveProbeAndDisablesBothCommands()
    {
        var api = new FakeEagleApiClient();
        TaskCompletionSource releaseProbe = api.BlockNextProbe();
        var viewModel = new EagleSettingsViewModel(
            new FakeEagleExportTargetStore(),
            new FakeEntryIntegrationPolicyService(isEnabled: true),
            api);
        Task running = viewModel.TestCommand.ExecuteAsync();
        await api.ProbeStarted.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            IDisposable disposable =
                Assert.IsAssignableFrom<IDisposable>(viewModel);
            disposable.Dispose();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => running);
            Assert.False(viewModel.SaveCommand.CanExecute(null));
            Assert.False(viewModel.TestCommand.CanExecute(null));
        }
        finally
        {
            releaseProbe.TrySetResult();
            if (!running.IsCompleted)
            {
                await running;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAndTestShareOneBusyGate(bool saveStartsFirst)
    {
        var store = new FakeEagleExportTargetStore();
        var api = new FakeEagleApiClient();
        TaskCompletionSource releaseProbe = api.BlockNextProbe();
        var viewModel = new EagleSettingsViewModel(
            store,
            new FakeEntryIntegrationPolicyService(isEnabled: true),
            api);

        Task first = saveStartsFirst
            ? viewModel.SaveCommand.ExecuteAsync()
            : viewModel.TestCommand.ExecuteAsync();
        await api.ProbeStarted.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            // 两个按钮共享同一份状态，任何探测进行中都不能再保存或重复测试。
            Assert.True(viewModel.IsBusy);
            Assert.False(viewModel.SaveCommand.CanExecute(null));
            Assert.False(viewModel.TestCommand.CanExecute(null));

            if (saveStartsFirst)
            {
                await viewModel.TestCommand.ExecuteAsync();
            }
            else
            {
                await viewModel.SaveCommand.ExecuteAsync();
            }

            Assert.Equal(saveStartsFirst ? 1 : 0, store.SaveCalls);
            Assert.Single(api.ProbedEndpoints);
        }
        finally
        {
            releaseProbe.TrySetResult();
            await first;
        }

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.True(viewModel.TestCommand.CanExecute(null));
    }

    private sealed class FakeEagleExportTargetStore
        : IEagleExportTargetStore
    {
        public EagleExportTarget? Current { get; init; }
        public EagleExportTarget? Saved { get; private set; }
        public int SaveCalls { get; private set; }
        public Exception? SaveFailure { get; init; }

        public Task<EagleExportTarget?> GetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task<IEagleExportTargetLease> AcquireExportLeaseAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IEagleExportTargetLease>(
                new FakeTargetLease(Current));

        public Task SaveAsync(
            EagleExportTarget target,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            if (SaveFailure is not null)
            {
                return Task.FromException(SaveFailure);
            }
            Saved = target;
            return Task.CompletedTask;
        }

        private sealed class FakeTargetLease(
            EagleExportTarget? target)
            : IEagleExportTargetLease
        {
            public EagleExportTarget? Target { get; } = target;

            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;
        }
    }

    private sealed class FakeEntryIntegrationPolicyService(
        bool isEnabled)
        : IEntryIntegrationPolicyService
    {
        public List<EntryIntegrationPolicyScope> Scopes { get; } = [];
        public Exception? Failure { get; init; }

        public Task<EntryIntegrationPolicySnapshot> GetAsync(
            EntryIntegrationPolicyScope scope,
            CancellationToken cancellationToken)
        {
            Scopes.Add(scope);
            if (Failure is not null)
            {
                return Task.FromException<
                    EntryIntegrationPolicySnapshot>(Failure);
            }
            IReadOnlyList<EntryIntegrationPolicy> policies =
                isEnabled
                    ? [new(EntryIntegrationKind.Eagle, true, [])]
                    : [];
            return Task.FromResult(new EntryIntegrationPolicySnapshot(
                7,
                policies,
                scope));
        }

        public Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
            IReadOnlyList<EntryIntegrationPolicyInput> inputs,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeEagleApiClient : IEagleApiClient
    {
        private TaskCompletionSource _probeStarted =
            CompletedSignal();
        private TaskCompletionSource? _probeRelease;

        public EagleApiCapability Result { get; init; } =
            new(
                "4.0.0",
                21,
                "111111111111111111111111");
        public Exception? Failure { get; init; }
        public List<Uri> ProbedEndpoints { get; } = [];
        public Task ProbeStarted => _probeStarted.Task;

        public TaskCompletionSource BlockNextProbe()
        {
            _probeStarted = NewSignal();
            _probeRelease = NewSignal();
            return _probeRelease;
        }

        public async Task<EagleApiCapability> ProbeAsync(
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            ProbedEndpoints.Add(endpoint);
            _probeStarted.TrySetResult();
            if (_probeRelease is not null)
            {
                await _probeRelease.Task.WaitAsync(cancellationToken);
                _probeRelease = null;
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return Result;
        }

        public Task<bool> ExistsAsync(
            Uri endpoint,
            string itemId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<string> AddAsync(
            Uri endpoint,
            EagleAddItem item,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource CompletedSignal()
        {
            TaskCompletionSource signal = NewSignal();
            signal.SetResult();
            return signal;
        }
    }
}
