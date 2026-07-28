using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class FeedSmartViewAdminService(
    WorkerAccountSessionService accountSession)
    : IFeedSmartViewAdminService
{
    public async Task<FeedSmartViewSnapshot> GetAllAsync(
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await accountSession.GetAuthorizedAsync(
                "/v1/smart-views?scope=ALL",
                cancellationToken).ConfigureAwait(false);
        await WorkerAccountSessionService.EnsureSuccessAsync(
            response,
            cancellationToken).ConfigureAwait(false);
        FeedSmartViewWireProtocol.SnapshotDto dto =
            await FeedSmartViewWireProtocol.ReadAsync<
                FeedSmartViewWireProtocol.SnapshotDto>(
                response,
                cancellationToken).ConfigureAwait(false);
        try
        {
            return FeedSmartViewWireProtocol.MapSnapshot(
                dto,
                FeedSmartViewScope.All,
                lastSyncedAt: null);
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
                FeedSmartViewWireProtocol.InvalidResponse().Error,
                exception);
        }
    }

    public Task<FeedSmartViewMutationResult> CreateAsync(
        FeedSmartViewInput input,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        MutateAsync(
            HttpMethod.Post,
            "/v1/admin/smart-views",
            input,
            expectedVersion,
            ExpectedViewId: null,
            cancellationToken);

    public Task<FeedSmartViewMutationResult> UpdateAsync(
        string viewId,
        FeedSmartViewInput input,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        string id = FeedSmartViewWireProtocol.ValidateId(viewId);
        return MutateAsync(
            HttpMethod.Patch,
            $"/v1/admin/smart-views/{id}",
            input,
            expectedVersion,
            id,
            cancellationToken);
    }

    public async Task<FeedSmartViewMutationResult> DeleteAsync(
        string viewId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        string id = FeedSmartViewWireProtocol.ValidateId(viewId);
        FeedSmartViewWireProtocol.ValidateVersion(expectedVersion);
        using HttpResponseMessage response =
            await accountSession.SendSmartViewMutationAsync(
                HttpMethod.Delete,
                $"/v1/admin/smart-views/{id}",
                expectedVersion,
                payload: null,
                cancellationToken).ConfigureAwait(false);
        await WorkerAccountSessionService.EnsureSuccessAsync(
            response,
            cancellationToken).ConfigureAwait(false);
        FeedSmartViewWireProtocol.MutationDto dto =
            await FeedSmartViewWireProtocol.ReadAsync<
                FeedSmartViewWireProtocol.MutationDto>(
                response,
                cancellationToken).ConfigureAwait(false);
        if (dto.ViewSetVersion != expectedVersion + 1
            || dto.ViewSetVersion
                > FeedSmartViewWireProtocol.MaximumSafeInteger
            || dto.View is not null
            || !string.Equals(
                dto.DeletedViewId,
                id,
                StringComparison.Ordinal))
        {
            throw FeedSmartViewWireProtocol.InvalidResponse();
        }
        return new(dto.ViewSetVersion, null, id);
    }

    private async Task<FeedSmartViewMutationResult> MutateAsync(
        HttpMethod method,
        string path,
        FeedSmartViewInput input,
        long expectedVersion,
        string? ExpectedViewId,
        CancellationToken cancellationToken)
    {
        FeedSmartViewWireProtocol.ValidateVersion(expectedVersion);
        FeedSmartViewInput normalized =
            FeedSmartViewValidator.ValidateAndNormalize(input);
        using HttpResponseMessage response =
            await accountSession.SendSmartViewMutationAsync(
                method,
                path,
                expectedVersion,
                FeedSmartViewWireProtocol.ToPayload(normalized),
                cancellationToken).ConfigureAwait(false);
        await WorkerAccountSessionService.EnsureSuccessAsync(
            response,
            cancellationToken).ConfigureAwait(false);
        FeedSmartViewWireProtocol.MutationDto dto =
            await FeedSmartViewWireProtocol.ReadAsync<
                FeedSmartViewWireProtocol.MutationDto>(
                response,
                cancellationToken).ConfigureAwait(false);
        try
        {
            FeedSmartView view =
                FeedSmartViewWireProtocol.MapView(dto.View);
            if (dto.ViewSetVersion != expectedVersion + 1
                || dto.ViewSetVersion
                    > FeedSmartViewWireProtocol.MaximumSafeInteger
                || dto.DeletedViewId is not null
                || (ExpectedViewId is not null
                    && !string.Equals(
                        ExpectedViewId,
                        view.Id,
                        StringComparison.Ordinal)))
            {
                throw FeedSmartViewWireProtocol.InvalidResponse();
            }
            return new(dto.ViewSetVersion, view);
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
                FeedSmartViewWireProtocol.InvalidResponse().Error,
                exception);
        }
    }
}
