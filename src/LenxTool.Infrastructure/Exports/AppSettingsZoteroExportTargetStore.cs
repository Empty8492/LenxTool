using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Exports;

/// <summary>
/// 将 Zotero 非敏感目标保存为单个带版本 JSON 文档；API key 仍由 DPAPI 凭据存储管理。
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "进程级异步代际门必须比活动导出租约存活更久；关闭时处置会与租约释放竞态。")]
public sealed class AppSettingsZoteroExportTargetStore(
    IAppSettingsRepository settings)
    : IZoteroExportTargetStore
{
    private const int DocumentVersion = 1;
    private const int MaximumSettingsDocumentLength = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _exportGenerationGate = new(1, 1);

    public const string SettingsKey = "integration.zotero.target.v1";

    public async Task<ZoteroExportTarget?> GetAsync(
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
        catch (Exception exception)
            when (exception is JsonException
                  or ArgumentException
                  or InvalidOperationException
                  or NotSupportedException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        ZoteroExportTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ZoteroExportTarget normalized = Normalize(target);
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

    public async Task<IZoteroExportTargetLease> AcquireExportLeaseAsync(
        CancellationToken cancellationToken)
    {
        await _exportGenerationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ZoteroExportTarget? target =
                await GetAsync(cancellationToken).ConfigureAwait(false);
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

    internal static ZoteroExportTarget Normalize(
        ZoteroExportTarget target)
    {
        ZoteroExportTarget.Validate(target);
        return target;
    }

    private static IOException StorageFailure(
        string operation,
        Exception exception) =>
        new($"Zotero 导出设置暂时无法{operation}。", exception);

    private sealed record StoredDocument(
        int Version,
        ZoteroExportTarget? Target);

    private sealed class ExportTargetLease(
        ZoteroExportTarget? target,
        SemaphoreSlim gate)
        : IZoteroExportTargetLease
    {
        private int _isDisposed;

        public ZoteroExportTarget? Target { get; } = target;

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
