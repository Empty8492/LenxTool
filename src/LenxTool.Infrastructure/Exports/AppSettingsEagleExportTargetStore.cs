using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Infrastructure.Networking;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 将 Eagle 目标保存为一个带版本的 JSON 文档，避免端点字段分开更新产生半配置状态。
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "进程级异步代际门必须比活动导出租约存活更久；关闭时处置会与租约释放竞态。")]
public sealed class AppSettingsEagleExportTargetStore(
    IAppSettingsRepository settings)
    : IEagleExportTargetStore
{
    private const int DocumentVersion = 1;
    private const int MaximumSettingsDocumentLength = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _exportGenerationGate = new(1, 1);

    public const string SettingsKey = "integration.eagle.target.v1";

    public async Task<EagleExportTarget?> GetAsync(
        CancellationToken cancellationToken)
    {
        string? json;
        try
        {
            json = await settings.GetAsync(
                    SettingsKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw StorageFailure("读取", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw StorageFailure("读取", exception);
        }

        if (string.IsNullOrWhiteSpace(json)
            || Encoding.UTF8.GetByteCount(json)
                > MaximumSettingsDocumentLength)
        {
            return null;
        }

        try
        {
            StoredDocument? document =
                JsonSerializer.Deserialize<StoredDocument>(
                    json,
                    JsonOptions);
            return document is { Version: DocumentVersion, Target: not null }
                ? Normalize(document.Target)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        EagleExportTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        EagleExportTarget normalized = Normalize(target);
        string json = JsonSerializer.Serialize(
            new StoredDocument(DocumentVersion, normalized),
            JsonOptions);

        await _exportGenerationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await settings.SetAsync(
                    SettingsKey,
                    json,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw StorageFailure("保存", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw StorageFailure("保存", exception);
        }
        finally
        {
            _exportGenerationGate.Release();
        }
    }

    public async Task<IEagleExportTargetLease> AcquireExportLeaseAsync(
        CancellationToken cancellationToken)
    {
        await _exportGenerationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EagleExportTarget? target = await GetAsync(cancellationToken)
                .ConfigureAwait(false);
            return new ExportTargetLease(
                target,
                _exportGenerationGate);
        }
        catch
        {
            _exportGenerationGate.Release();
            throw;
        }
    }

    internal static EagleExportTarget Normalize(
        EagleExportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(
                target.TargetId,
                EagleExportTarget.DefaultTargetId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Eagle 当前只支持默认导出目标。",
                nameof(target));
        }

        return target with
        {
            Endpoint = EagleApiClient.ValidateEndpoint(target.Endpoint)
        };
    }

    private static IOException StorageFailure(
        string operation,
        Exception exception) =>
        new($"Eagle 导出设置暂时无法{operation}。", exception);

    private sealed record StoredDocument(
        int Version,
        EagleExportTarget? Target);

    private sealed class ExportTargetLease(
        EagleExportTarget? target,
        SemaphoreSlim gate)
        : IEagleExportTargetLease
    {
        private int _isDisposed;

        public EagleExportTarget? Target { get; } = target;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                gate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
