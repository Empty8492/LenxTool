using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Infrastructure.SystemServices;

namespace LenxTool.Infrastructure.Security;

public sealed partial class DpapiSecretStore : ISecretStore, IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LenxTool.Secrets.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DpapiSecretStore(AppPaths paths)
    {
        paths.EnsureCreated();
        StoragePath = Path.Combine(paths.SecretsDirectory, "secrets.dat");
    }

    public string StoragePath { get; }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> values = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return values.GetValueOrDefault(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) > 16 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "单个密钥值不能超过 16 KiB。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> values = await LoadAsync(cancellationToken).ConfigureAwait(false);
            values[name] = value;
            await SaveAsync(values, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> values = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (values.Remove(name))
            {
                await SaveAsync(values, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gate.Dispose();
        _disposed = true;
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(StoragePath)) return new(StringComparer.Ordinal);

        try
        {
            byte[] encrypted = await File.ReadAllBytesAsync(StoragePath, cancellationToken).ConfigureAwait(false);
            byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(clear)
                ?? new(StringComparer.Ordinal);
        }
        catch (CryptographicException exception)
        {
            throw CreateSecretStoreException(exception);
        }
        catch (JsonException exception)
        {
            throw CreateSecretStoreException(exception);
        }
    }

    private async Task SaveAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(values);
        byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
        string temporaryPath = StoragePath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, encrypted, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, StoragePath, overwrite: true);
    }

    private static AppException CreateSecretStoreException(Exception exception) =>
        new(
            new(
                AppErrorCode.AccessDenied,
                "无法读取本机密钥",
                "密钥文件不属于当前 Windows 用户、已损坏或无法解密。",
                "请确认正在使用原 Windows 账号；必要时重新填写 API Key。",
                exception.Message,
                "Windows DPAPI"),
            exception);

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!SecretNamePattern().IsMatch(name))
        {
            throw new ArgumentException("密钥名称只能包含小写字母、数字、点、下划线和连字符。", nameof(name));
        }
    }

    [GeneratedRegex("^[a-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNamePattern();
}
