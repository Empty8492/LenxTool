using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 将同一 FeedEntry 确定性映射到同一个 Outline 文档；内容变化更新原文档而不新建副本。
/// </summary>
public sealed class OutlineEntryExporter(
    IIntegrationExportTargetStore<OutlineExportTarget> targets,
    IEntryIntegrationPolicyService policies,
    IEntryIntegrationCredentialStore credentials,
    IEntryIntegrationEndpointAuthorizer authorizer,
    IOutlineApiClient api)
    : IEntryExporter
{
    private static readonly Guid DocumentNamespace =
        Guid.Parse("b3a0c96a-9fa4-5adc-b7f5-97a3c8389b10");
    internal const int MaximumMarkdownBytes = 64 * 1024;

    public const string ExporterId = "outline";

    public EntryExportCapability Capability { get; } = new(
        ExporterId,
        "Outline",
        Array.AsReadOnly(Enum.GetValues<EntryViewKind>()),
        RequiresCredentials: true,
        MaximumMarkdownBytes,
        IsIdempotent: true);

    public async Task<EntryExportResult> ExportAsync(
        EntryExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                request.ExporterId,
                ExporterId,
                StringComparison.Ordinal)
            || !OutlineExportTarget.IsSupportedQueueTargetId(
                request.TargetId))
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }

        await using IIntegrationExportTargetLease<OutlineExportTarget> lease =
            await targets.AcquireExportLeaseAsync(cancellationToken)
                .ConfigureAwait(false);
        OutlineExportTarget target = lease.Target is null
            ? throw Failure(EntryExportErrorCode.Conflict)
            : OutlineExportTarget.Normalize(lease.Target);
        if (!target.MatchesQueueTargetId(request.TargetId))
        {
            throw Failure(EntryExportErrorCode.Conflict);
        }
        if (target.CredentialVersion != 1)
        {
            throw Failure(EntryExportErrorCode.CredentialsRequired);
        }

        string markdown;
        try
        {
            markdown = MarkdownDocumentRenderer.Render(
                request.Entry,
                request.ViewKind,
                MarkdownExportContentMode.Content,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new(
                    TemplateMarkdown: null,
                    Tags: [],
                    IncludeSourceLink: true),
                MaximumMarkdownBytes);
        }
        catch (MarkdownRenderLimitExceededException exception)
        {
            throw Failure(
                EntryExportErrorCode.ContentTooLarge,
                exception);
        }

        EntryIntegrationPolicySnapshot snapshot = await policies.GetAsync(
                EntryIntegrationPolicyScope.Active,
                cancellationToken)
            .ConfigureAwait(false);
        EntryIntegrationPolicy? policy = snapshot.Policies.SingleOrDefault(
            value => value.Kind == EntryIntegrationKind.Outline
                && value.IsEnabled);
        string collectionId = target.CollectionId.ToString("D");
        if (policy is null
            || !policy.AllowedResources.Contains(
                collectionId,
                StringComparer.Ordinal))
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }

        EntryIntegrationProbeContext? context =
            await authorizer.AuthorizeAsync(
                    new(
                        OutlineExportTarget.DefaultTargetId,
                        EntryIntegrationKind.Outline,
                        target.Endpoint),
                    policy,
                    cancellationToken)
                .ConfigureAwait(false);
        if (context is null)
        {
            throw Failure(EntryExportErrorCode.AccessDenied);
        }
        string? token = await credentials.GetAsync(
                EntryIntegrationKind.Outline,
                OutlineExportTarget.DefaultTargetId,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw Failure(EntryExportErrorCode.CredentialsRequired);
        }

        var document = new OutlineDocument(
            CreateDocumentId(request.TargetId, request.Entry.Id),
            target.CollectionId,
            NormalizeTitle(request.Entry.Title),
            markdown);
        try
        {
            OutlineDocumentResult result = await api.UpsertAsync(
                    context,
                    token,
                    document,
                    cancellationToken)
                .ConfigureAwait(false);
            return EntryExportResult.Success(
                request.IdempotencyKey,
                result.Id.ToString("D"),
                result.Url);
        }
        catch (OutlineApiException exception)
            when (exception.Failure == OutlineApiFailure.Cancelled
                && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OutlineApiException exception)
        {
            throw MapFailure(exception);
        }
    }

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "UUID v5 requires SHA-1 for deterministic identity; it is not used for security.")]
    public static Guid CreateDocumentId(
        string queueTargetId,
        string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueTargetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        byte[] namespaceBytes = DocumentNamespace.ToByteArray();
        SwapGuidByteOrder(namespaceBytes);
        byte[] nameBytes = Encoding.UTF8.GetBytes(
            $"{queueTargetId}\n{entryId}");
        byte[] input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);
        byte[] hash = SHA1.HashData(input);
        byte[] result = hash[..16];
        result[6] = (byte)((result[6] & 0x0f) | 0x50);
        result[8] = (byte)((result[8] & 0x3f) | 0x80);
        SwapGuidByteOrder(result);
        return new Guid(result);
    }

    private static void SwapGuidByteOrder(Span<byte> value)
    {
        value[..4].Reverse();
        value.Slice(4, 2).Reverse();
        value.Slice(6, 2).Reverse();
    }

    private static string NormalizeTitle(string value)
    {
        string title = string.Join(
            ' ',
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) return "无标题条目";
        return title.Length <= 1024 ? title : title[..1024];
    }

    private static EntryExportException MapFailure(
        OutlineApiException exception) =>
        exception.Failure switch
        {
            OutlineApiFailure.Unauthorized
                or OutlineApiFailure.BlockedEndpoint =>
                Failure(EntryExportErrorCode.AccessDenied),
            OutlineApiFailure.Rejected =>
                Failure(EntryExportErrorCode.ProviderRejected),
            OutlineApiFailure.Conflict =>
                Failure(EntryExportErrorCode.Conflict),
            OutlineApiFailure.RateLimited =>
                Failure(
                    EntryExportErrorCode.RateLimited,
                    isRetryable: true,
                    retryAfter: exception.RetryAfter),
            OutlineApiFailure.Unavailable
                or OutlineApiFailure.UnknownWriteOutcome
                or OutlineApiFailure.Cancelled =>
                Failure(
                    EntryExportErrorCode.DestinationUnavailable,
                    isRetryable: true,
                    retryAfter: exception.RetryAfter),
            _ => Failure(EntryExportErrorCode.Unknown)
        };

    private static EntryExportException Failure(
        EntryExportErrorCode code,
        Exception? inner = null,
        bool isRetryable = false,
        TimeSpan? retryAfter = null) =>
        new(new(code, isRetryable, retryAfter), inner);
}
