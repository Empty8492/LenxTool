using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 在每次执行时重新读取本地目标与 ACTIVE 管理策略，并把条目写入
/// 用户明确授权的 Vault 子目录。该适配器不会启动 Obsidian URI。
/// </summary>
public sealed class ObsidianEntryExporter
    : IEntryExporter, IDisposable
{
    private readonly IObsidianExportTargetStore _targets;
    private readonly IEntryIntegrationPolicyService _policies;
    private readonly IEntryAssetStore? _assetStore;
    private readonly Func<string, string?, string>
        _resolveExportDirectory;
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    public const string ExporterId = "obsidian";
    public const string TargetId = ObsidianExportTarget.DefaultTargetId;

    public ObsidianEntryExporter(
        IObsidianExportTargetStore targets,
        IEntryIntegrationPolicyService policies,
        IEntryAssetStore? assetStore)
        : this(
            targets,
            policies,
            assetStore,
            MarkdownExportPathPolicy.ResolveContainedDirectory)
    {
    }

    internal ObsidianEntryExporter(
        IObsidianExportTargetStore targets,
        IEntryIntegrationPolicyService policies,
        IEntryAssetStore? assetStore,
        Func<string, string?, string> resolveExportDirectory)
    {
        _targets = targets
            ?? throw new ArgumentNullException(nameof(targets));
        _policies = policies
            ?? throw new ArgumentNullException(nameof(policies));
        _assetStore = assetStore;
        _resolveExportDirectory = resolveExportDirectory
            ?? throw new ArgumentNullException(
                nameof(resolveExportDirectory));
    }

    public EntryExportCapability Capability { get; } = new(
        ExporterId,
        "Obsidian",
        Array.AsReadOnly(Enum.GetValues<EntryViewKind>()),
        RequiresCredentials: false,
        MarkdownEntryExporter.MaximumContentBytes,
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
            || !string.Equals(
                request.TargetId,
                request.TargetId.Trim(),
                StringComparison.Ordinal)
            || !ObsidianExportTarget.IsSupportedQueueTargetId(
                request.TargetId))
        {
            throw Failure(EntryExportErrorCode.InvalidRequest);
        }

        await _exportGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // 两项状态都在执行时读取，防止旧队列任务绕过新撤销的策略或设置。
            ObsidianExportTarget? configuredTarget;
            EntryIntegrationPolicySnapshot snapshot;
            try
            {
                configuredTarget =
                    await _targets.GetAsync(cancellationToken)
                        .ConfigureAwait(false);
                snapshot = await _policies.GetAsync(
                            EntryIntegrationPolicyScope.Active,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (AppException exception)
            {
                throw MapDependencyFailure(exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure(
                    EntryExportErrorCode.AccessDenied,
                    exception);
            }
            catch (IOException exception)
            {
                throw Failure(
                    EntryExportErrorCode.DestinationUnavailable,
                    exception,
                    isRetryable: true);
            }
            if (!snapshot.Policies.Any(policy =>
                    policy.Kind == EntryIntegrationKind.Obsidian
                    && policy.IsEnabled))
            {
                throw Failure(EntryExportErrorCode.AccessDenied);
            }
            if (configuredTarget is null)
            {
                throw Failure(
                    EntryExportErrorCode.DestinationUnavailable);
            }

            ObsidianExportTarget target;
            try
            {
                target =
                    AppSettingsObsidianExportTargetStore.Normalize(
                        configuredTarget,
                        missingVaultIsTransient: true);
            }
            catch (InvalidOperationException exception)
            {
                throw Failure(
                    EntryExportErrorCode.AccessDenied,
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure(
                    EntryExportErrorCode.AccessDenied,
                    exception);
            }
            catch (IOException exception)
            {
                throw Failure(
                    EntryExportErrorCode.DestinationUnavailable,
                    exception,
                    isRetryable: true);
            }
            catch (ArgumentException exception)
            {
                throw Failure(
                    EntryExportErrorCode.InvalidRequest,
                    exception);
            }
            // Only persisted pre-version jobs may inherit the current target.
            // A versioned job must never cross into a newly configured Vault.
            bool isLegacyTargetId = string.Equals(
                request.TargetId,
                ObsidianExportTarget.DefaultTargetId,
                StringComparison.Ordinal);
            if (!isLegacyTargetId
                && !string.Equals(
                    request.TargetId,
                    target.CreateQueueTargetId(),
                    StringComparison.Ordinal))
            {
                throw Failure(EntryExportErrorCode.Conflict);
            }

            string exportDirectory;
            try
            {
                exportDirectory =
                    _resolveExportDirectory(
                        target.VaultRootPath,
                        target.RelativeDirectory);
            }
            catch (InvalidOperationException exception)
            {
                throw Failure(
                    EntryExportErrorCode.AccessDenied,
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure(
                    EntryExportErrorCode.AccessDenied,
                    exception);
            }
            catch (IOException exception)
            {
                throw Failure(
                    EntryExportErrorCode.DestinationUnavailable,
                    exception,
                    isRetryable: true);
            }
            catch (ArgumentException exception)
            {
                throw Failure(
                    EntryExportErrorCode.InvalidRequest,
                    exception);
            }

            var markdownTarget = new MarkdownExportTarget(
                request.TargetId,
                exportDirectory,
                MarkdownExportContentMode.Content,
                MarkdownExistingFileBehavior.CreateNewVersion)
            {
                RenderOptions = new(
                    target.TemplateMarkdown,
                    target.Tags,
                    target.IncludeSourceLink)
            };
            using var markdownExporter = new MarkdownEntryExporter(
                [markdownTarget],
                _assetStore);
            EntryExportResult result =
                await markdownExporter.ExportAsync(
                        request with
                        {
                            ExporterId =
                                MarkdownEntryExporter.ExporterId
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            return result with
            {
                IdempotencyKey = request.IdempotencyKey
            };
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private static EntryExportException Failure(
        EntryExportErrorCode code,
        Exception? innerException = null,
        bool isRetryable = false) =>
        new(
            new(
                code,
                isRetryable),
            innerException);

    private static EntryExportException MapDependencyFailure(
        AppException exception)
    {
        EntryExportErrorCode code = exception.Error.Code
            is AppErrorCode.AccessDenied
                or AppErrorCode.CredentialsInvalid
            ? EntryExportErrorCode.AccessDenied
            : EntryExportErrorCode.DestinationUnavailable;
        return Failure(
            code,
            exception,
            exception.Error.IsRetryable);
    }

    public void Dispose() => _exportGate.Dispose();
}
