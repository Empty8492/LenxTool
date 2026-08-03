using System.IO;
using System.Net.Http;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.App.ViewModels;

/// <summary>
/// 管理本机 Eagle Web API 端点；本地配置允许预先保存，任何探测仍受
/// ACTIVE 管理员策略约束。
/// </summary>
public sealed class EagleSettingsViewModel : ObservableObject, IDisposable
{
    private const string DefaultEndpoint =
        "http://127.0.0.1:41595/";
    private readonly IEagleExportTargetStore _targetStore;
    private readonly IEntryIntegrationPolicyService _policies;
    private readonly IEagleApiClient _api;
    private string _endpointText = DefaultEndpoint;
    private string _status =
        "Eagle 端点仅保存在本机；管理员启用后才会探测应用和资源库。";
    private bool _isBusy;

    public EagleSettingsViewModel(
        IEagleExportTargetStore targetStore,
        IEntryIntegrationPolicyService policies,
        IEagleApiClient api)
    {
        _targetStore = targetStore
            ?? throw new ArgumentNullException(nameof(targetStore));
        _policies = policies
            ?? throw new ArgumentNullException(nameof(policies));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        SaveCommand = new(SaveAsync, CanOperate);
        TestCommand = new(TestAsync, CanOperate);
    }

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand TestCommand { get; }

    public string EndpointText
    {
        get => _endpointText;
        set => SetProperty(ref _endpointText, value ?? string.Empty);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            SaveCommand.NotifyCanExecuteChanged();
            TestCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanOperate() => !IsBusy;

    /// <summary>
    /// 恢复本机保存的目标；初始化只读配置，不会探测 Eagle。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            EagleExportTarget? target =
                await _targetStore.GetAsync(cancellationToken);
            if (target is null)
            {
                Status =
                    "尚未保存 Eagle 端点，当前使用默认本机地址。";
                return;
            }

            EndpointText = target.Endpoint.AbsoluteUri;
            Status = "已从本机加载 Eagle 端点；尚未发起连接。";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            Status = "Eagle 本机设置暂时无法读取。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Uri endpoint;
        try
        {
            endpoint = BuildEndpoint();
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
            return;
        }

        IsBusy = true;
        try
        {
            if (!await TrySaveTargetAsync(endpoint, cancellationToken))
            {
                return;
            }

            bool? isEnabled = await TryReadActivePolicyAsync(
                endpointWasSaved: true,
                cancellationToken);
            if (isEnabled is null)
            {
                return;
            }
            if (!isEnabled.Value)
            {
                Status =
                    "本机端点已保存；请等待管理员启用 Eagle 后再测试。";
                return;
            }

            await ProbeAndReportAsync(
                endpoint,
                endpointWasSaved: true,
                cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestAsync(CancellationToken cancellationToken)
    {
        Uri endpoint;
        try
        {
            endpoint = BuildEndpoint();
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
            return;
        }

        IsBusy = true;
        try
        {
            bool? isEnabled = await TryReadActivePolicyAsync(
                endpointWasSaved: false,
                cancellationToken);
            if (isEnabled is null)
            {
                return;
            }
            if (!isEnabled.Value)
            {
                Status = "管理员尚未启用 Eagle，未发起连接测试。";
                return;
            }

            // 显式测试只使用当前输入，不隐式覆盖已经保存的端点。
            await ProbeAndReportAsync(
                endpoint,
                endpointWasSaved: false,
                cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Uri BuildEndpoint()
    {
        if (!Uri.TryCreate(
                EndpointText.Trim(),
                UriKind.Absolute,
                out Uri? endpoint))
        {
            throw new ArgumentException(
                "Eagle 端点必须是带显式端口的 loopback HTTP 根地址。");
        }

        return EagleApiClient.ValidateEndpoint(endpoint);
    }

    private async Task<bool> TrySaveTargetAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            // 保存和探测刻意分开：管理员禁用时仍可预配本机端点，但不得联网。
            await _targetStore.SaveAsync(
                new EagleExportTarget(
                    EagleExportTarget.DefaultTargetId,
                    endpoint),
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Status = "保存 Eagle 本机设置超时，端点未保存。";
            return false;
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            Status = "保存 Eagle 本机设置时无法访问本地存储。";
            return false;
        }
    }

    private async Task<bool?> TryReadActivePolicyAsync(
        bool endpointWasSaved,
        CancellationToken cancellationToken)
    {
        try
        {
            return await IsActivePolicyEnabledAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Status = endpointWasSaved
                ? "本机端点已保存；读取管理员策略超时，未发起 Eagle 连接。"
                : "读取管理员策略超时，未发起 Eagle 连接测试。";
            return null;
        }
        catch (Exception exception)
            when (exception is AppException
                  or HttpRequestException
                  or IOException
                  or UnauthorizedAccessException)
        {
            Status = endpointWasSaved
                ? "本机端点已保存；暂时无法读取管理员策略，未发起 Eagle 连接。"
                : "暂时无法读取管理员策略，未发起 Eagle 连接测试。";
            return null;
        }
    }

    private async Task<bool> IsActivePolicyEnabledAsync(
        CancellationToken cancellationToken)
    {
        EntryIntegrationPolicySnapshot snapshot = await _policies.GetAsync(
            EntryIntegrationPolicyScope.Active,
            cancellationToken);
        return snapshot.Policies.Any(policy =>
            policy.Kind == EntryIntegrationKind.Eagle
            && policy.IsEnabled);
    }

    private async Task ProbeAndReportAsync(
        Uri endpoint,
        bool endpointWasSaved,
        CancellationToken cancellationToken)
    {
        try
        {
            EagleApiCapability capability = await _api.ProbeAsync(
                endpoint,
                cancellationToken);
            Status = PrefixSavedStatus(
                $"Eagle 应用与资源库检查通过（{capability.Version}，Build {capability.BuildNumber}）。",
                endpointWasSaved);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Status = PrefixSavedStatus(
                "连接 Eagle 应用或资源库超时。",
                endpointWasSaved);
        }
        catch (EagleApiException exception)
        {
            Status = PrefixSavedStatus(
                DescribeApiFailure(exception.Failure),
                endpointWasSaved);
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                  or IOException
                  or UnauthorizedAccessException)
        {
            Status = PrefixSavedStatus(
                "无法连接 Eagle 应用或资源库，请确认 Eagle 已启动。",
                endpointWasSaved);
        }
    }

    private static string PrefixSavedStatus(
        string message,
        bool endpointWasSaved) =>
        endpointWasSaved
            ? $"本机端点已保存；{message}"
            : message;

    private static string DescribeApiFailure(EagleApiFailure failure) =>
        failure switch
        {
            EagleApiFailure.Incompatible =>
                "Eagle 应用或资源库不满足 Web API V2 要求。",
            EagleApiFailure.Rejected =>
                "Eagle 拒绝了检查请求，请确认应用和资源库状态。",
            _ => "无法连接 Eagle 应用或资源库，请确认 Eagle 已启动。"
        };

    public void Dispose()
    {
        SaveCommand.Dispose();
        TestCommand.Dispose();
    }
}
