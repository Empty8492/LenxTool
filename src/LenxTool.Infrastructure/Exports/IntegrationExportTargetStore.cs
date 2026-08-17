using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace LenxTool.Infrastructure.Exports;

public interface IIntegrationExportTargetLease<out T>
    : IAsyncDisposable
    where T : class
{
    T? Target { get; }
}

/// <summary>
/// 每种外部适配器拥有独立的版本化设置文档；凭据不属于该文档。
/// </summary>
public interface IIntegrationExportTargetStore<T>
    where T : class
{
    Task<T?> GetAsync(CancellationToken cancellationToken);

    Task<IIntegrationExportTargetLease<T>> AcquireExportLeaseAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(T target, CancellationToken cancellationToken);
}

/// <summary>
/// 复用原子设置与导出代际门，但调用方必须为每种 T 提供独立 key 和规范化函数。
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "进程级代际门必须比活动导出租约存活更久。")]
public sealed class AppSettingsIntegrationExportTargetStore<T>(
    IAppSettingsRepository settings,
    string settingsKey,
    Func<T, T> normalize)
    : IIntegrationExportTargetStore<T>
    where T : class
{
    private const int DocumentVersion = 1;
    private const int MaximumDocumentBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly string _settingsKey = ValidateSettingsKey(settingsKey);
    private readonly Func<T, T> _normalize = normalize
        ?? throw new ArgumentNullException(nameof(normalize));

    public async Task<T?> GetAsync(CancellationToken cancellationToken)
    {
        string? json;
        try
        {
            json = await settings.GetAsync(
                    _settingsKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is SqliteException
                or InvalidOperationException)
        {
            throw StorageFailure("读取", exception);
        }
        if (string.IsNullOrWhiteSpace(json)
            || Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
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
                ? _normalize(document.Target)
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
        T target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        T normalized = _normalize(target);
        string json = JsonSerializer.Serialize(
            new StoredDocument(DocumentVersion, normalized),
            JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        await _generationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await settings.SetAsync(
                    _settingsKey,
                    json,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is SqliteException
                or InvalidOperationException)
        {
            throw StorageFailure("保存", exception);
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public async Task<IIntegrationExportTargetLease<T>>
        AcquireExportLeaseAsync(CancellationToken cancellationToken)
    {
        await _generationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            T? target = await GetAsync(cancellationToken)
                .ConfigureAwait(false);
            return new Lease(target, _generationGate);
        }
        catch
        {
            _generationGate.Release();
            throw;
        }
    }

    private static string ValidateSettingsKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || !value.StartsWith("integration.", StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "集成设置 key 无效。",
                nameof(value));
        }
        return value;
    }

    private static InvalidOperationException StorageFailure(
        string operation,
        Exception inner) =>
        new($"外部集成目标暂时无法{operation}。", inner);

    private sealed record StoredDocument(int Version, T? Target);

    private sealed class Lease(
        T? target,
        SemaphoreSlim generationGate)
        : IIntegrationExportTargetLease<T>
    {
        private int _released;
        public T? Target { get; } = target;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                generationGate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
