using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// 读取 Worker 共享策略并执行管理员整集替换；服务端仍是最终 RBAC 边界。
/// </summary>
public sealed class WorkerEntryIntegrationPolicyService(
    WorkerAccountSessionService accountSession)
    : IEntryIntegrationPolicyService
{
    public async Task<EntryIntegrationPolicySnapshot> GetAsync(
        EntryIntegrationPolicyScope scope,
        CancellationToken cancellationToken)
    {
        string wireScope = scope switch
        {
            EntryIntegrationPolicyScope.Active => "ACTIVE",
            EntryIntegrationPolicyScope.All => "ALL",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
        using HttpResponseMessage response =
            await accountSession.GetAuthorizedAsync(
                $"/v1/integration-policies?scope={wireScope}",
                cancellationToken,
                integrationPolicySchema:
                    EntryIntegrationPolicyWireProtocol
                        .PolicySchemaVersion.ToString(
                            System.Globalization.CultureInfo
                                .InvariantCulture))
                .ConfigureAwait(false);
        await WorkerAccountSessionService.EnsureSuccessAsync(
            response,
            cancellationToken).ConfigureAwait(false);
        EntryIntegrationPolicyWireProtocol.SnapshotDto dto =
            await EntryIntegrationPolicyWireProtocol.ReadAsync<
                EntryIntegrationPolicyWireProtocol.SnapshotDto>(
                response,
                cancellationToken).ConfigureAwait(false);
        try
        {
            return EntryIntegrationPolicyWireProtocol.MapSnapshot(
                dto,
                scope);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidDataException
                or InvalidOperationException)
        {
            throw new AppException(
                EntryIntegrationPolicyWireProtocol
                    .InvalidResponse().Error,
                exception);
        }
    }

    public async Task<EntryIntegrationPolicyMutationResult> ReplaceAsync(
        IReadOnlyList<EntryIntegrationPolicyInput> inputs,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        EntryIntegrationPolicyWireProtocol.ValidateVersion(
            expectedVersion);
        object payload =
            EntryIntegrationPolicyWireProtocol.ToPayload(inputs);
        using HttpResponseMessage response =
            await accountSession.SendIntegrationPolicyMutationAsync(
                expectedVersion,
                payload,
                cancellationToken).ConfigureAwait(false);
        await WorkerAccountSessionService.EnsureSuccessAsync(
            response,
            cancellationToken).ConfigureAwait(false);
        EntryIntegrationPolicyWireProtocol.MutationDto dto =
            await EntryIntegrationPolicyWireProtocol.ReadAsync<
                EntryIntegrationPolicyWireProtocol.MutationDto>(
                response,
                cancellationToken).ConfigureAwait(false);
        return EntryIntegrationPolicyWireProtocol.MapMutation(
            dto,
            expectedVersion);
    }
}
