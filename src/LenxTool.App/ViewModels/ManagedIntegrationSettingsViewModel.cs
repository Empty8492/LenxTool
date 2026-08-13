using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;

namespace LenxTool.App.ViewModels;

/// <summary>
/// P2-16～P2-19 的四个生产适配器使用独立版本化目标文档；凭据输入保存后立即清空且不回读。
/// </summary>
public sealed class ManagedIntegrationSettingsViewModel : ObservableObject
{
    private readonly IIntegrationExportTargetStore<ReadeckExportTarget> _readeck;
    private readonly IIntegrationExportTargetStore<OutlineExportTarget> _outline;
    private readonly IIntegrationExportTargetStore<QBittorrentExportTarget> _qbittorrent;
    private readonly IIntegrationExportTargetStore<WebhookExportTarget> _webhook;
    private readonly IEntryIntegrationCredentialStore _credentials;
    private readonly IEntryIntegrationHealthService _health;
    private bool _isBusy;
    private string _readeckEndpoint = string.Empty;
    private bool _readeckArchive;
    private string _readeckCredential = string.Empty;
    private bool _readeckHasCredential;
    private string _readeckStatus = "尚未加载 Readeck 设置。";
    private string _outlineEndpoint = string.Empty;
    private string _outlineCollectionId = string.Empty;
    private string _outlineCredential = string.Empty;
    private bool _outlineHasCredential;
    private string _outlineStatus = "尚未加载 Outline 设置。";
    private string _qbittorrentEndpoint = string.Empty;
    private string _qbittorrentCategory = string.Empty;
    private string _qbittorrentCredential = string.Empty;
    private bool _qbittorrentHasCredential;
    private string _qbittorrentStatus = "尚未加载 qBittorrent 设置。";
    private string _webhookEndpoint = string.Empty;
    private bool _webhookUseHmac = true;
    private string _webhookCredential = string.Empty;
    private bool _webhookHasCredential;
    private string _webhookStatus = "尚未加载 Webhook 设置。";

    public ManagedIntegrationSettingsViewModel(
        IIntegrationExportTargetStore<ReadeckExportTarget> readeck,
        IIntegrationExportTargetStore<OutlineExportTarget> outline,
        IIntegrationExportTargetStore<QBittorrentExportTarget> qbittorrent,
        IIntegrationExportTargetStore<WebhookExportTarget> webhook,
        IEntryIntegrationCredentialStore credentials,
        IEntryIntegrationHealthService health)
    {
        _readeck = readeck;
        _outline = outline;
        _qbittorrent = qbittorrent;
        _webhook = webhook;
        _credentials = credentials;
        _health = health;
        SaveReadeckCommand = new(SaveReadeckAsync, CanOperate);
        TestReadeckCommand = new(TestReadeckAsync, CanOperate);
        DeleteReadeckCredentialCommand = new(
            cancellationToken => DeleteCredentialAsync(
                EntryIntegrationKind.Readeck,
                token => DeactivateCredentialAsync(
                    _readeck,
                    static target => ReadeckExportTarget.Normalize(
                        target with { CredentialVersion = 0 }),
                    token),
                value => ReadeckHasCredential = value,
                value => ReadeckStatus = value,
                cancellationToken),
            CanOperate);
        SaveOutlineCommand = new(SaveOutlineAsync, CanOperate);
        TestOutlineCommand = new(TestOutlineAsync, CanOperate);
        DeleteOutlineCredentialCommand = new(
            cancellationToken => DeleteCredentialAsync(
                EntryIntegrationKind.Outline,
                token => DeactivateCredentialAsync(
                    _outline,
                    static target => OutlineExportTarget.Normalize(
                        target with { CredentialVersion = 0 }),
                    token),
                value => OutlineHasCredential = value,
                value => OutlineStatus = value,
                cancellationToken),
            CanOperate);
        SaveQBittorrentCommand = new(SaveQBittorrentAsync, CanOperate);
        TestQBittorrentCommand = new(TestQBittorrentAsync, CanOperate);
        DeleteQBittorrentCredentialCommand = new(
            cancellationToken => DeleteCredentialAsync(
                EntryIntegrationKind.QBittorrent,
                token => DeactivateCredentialAsync(
                    _qbittorrent,
                    static target => QBittorrentExportTarget.Normalize(
                        target with { CredentialVersion = 0 }),
                    token),
                value => QBittorrentHasCredential = value,
                value => QBittorrentStatus = value,
                cancellationToken),
            CanOperate);
        SaveWebhookCommand = new(SaveWebhookAsync, CanOperate);
        TestWebhookCommand = new(TestWebhookAsync, CanOperate);
        DeleteWebhookCredentialCommand = new(
            cancellationToken => DeleteCredentialAsync(
                EntryIntegrationKind.Webhook,
                token => DeactivateCredentialAsync(
                    _webhook,
                    static target => WebhookExportTarget.Normalize(
                        target with { CredentialVersion = 0 }),
                    token),
                value => WebhookHasCredential = value,
                value => WebhookStatus = value,
                cancellationToken),
            CanOperate);
    }

    public AsyncRelayCommand SaveReadeckCommand { get; }
    public AsyncRelayCommand TestReadeckCommand { get; }
    public AsyncRelayCommand DeleteReadeckCredentialCommand { get; }
    public AsyncRelayCommand SaveOutlineCommand { get; }
    public AsyncRelayCommand TestOutlineCommand { get; }
    public AsyncRelayCommand DeleteOutlineCredentialCommand { get; }
    public AsyncRelayCommand SaveQBittorrentCommand { get; }
    public AsyncRelayCommand TestQBittorrentCommand { get; }
    public AsyncRelayCommand DeleteQBittorrentCredentialCommand { get; }
    public AsyncRelayCommand SaveWebhookCommand { get; }
    public AsyncRelayCommand TestWebhookCommand { get; }
    public AsyncRelayCommand DeleteWebhookCredentialCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            foreach (AsyncRelayCommand command in Commands())
            {
                command.NotifyCanExecuteChanged();
            }
        }
    }

    public string ReadeckEndpoint { get => _readeckEndpoint; set => SetProperty(ref _readeckEndpoint, value ?? string.Empty); }
    public bool ReadeckArchive { get => _readeckArchive; set => SetProperty(ref _readeckArchive, value); }
    public string ReadeckCredential { get => _readeckCredential; set => SetProperty(ref _readeckCredential, value ?? string.Empty); }
    public bool ReadeckHasCredential { get => _readeckHasCredential; private set { if (SetProperty(ref _readeckHasCredential, value)) DeleteReadeckCredentialCommand.NotifyCanExecuteChanged(); } }
    public string ReadeckStatus { get => _readeckStatus; private set => SetProperty(ref _readeckStatus, value); }
    public string OutlineEndpoint { get => _outlineEndpoint; set => SetProperty(ref _outlineEndpoint, value ?? string.Empty); }
    public string OutlineCollectionId { get => _outlineCollectionId; set => SetProperty(ref _outlineCollectionId, value ?? string.Empty); }
    public string OutlineCredential { get => _outlineCredential; set => SetProperty(ref _outlineCredential, value ?? string.Empty); }
    public bool OutlineHasCredential { get => _outlineHasCredential; private set { if (SetProperty(ref _outlineHasCredential, value)) DeleteOutlineCredentialCommand.NotifyCanExecuteChanged(); } }
    public string OutlineStatus { get => _outlineStatus; private set => SetProperty(ref _outlineStatus, value); }
    public string QBittorrentEndpoint { get => _qbittorrentEndpoint; set => SetProperty(ref _qbittorrentEndpoint, value ?? string.Empty); }
    public string QBittorrentCategory { get => _qbittorrentCategory; set => SetProperty(ref _qbittorrentCategory, value ?? string.Empty); }
    public string QBittorrentCredential { get => _qbittorrentCredential; set => SetProperty(ref _qbittorrentCredential, value ?? string.Empty); }
    public bool QBittorrentHasCredential { get => _qbittorrentHasCredential; private set { if (SetProperty(ref _qbittorrentHasCredential, value)) DeleteQBittorrentCredentialCommand.NotifyCanExecuteChanged(); } }
    public string QBittorrentStatus { get => _qbittorrentStatus; private set => SetProperty(ref _qbittorrentStatus, value); }
    public string WebhookEndpoint { get => _webhookEndpoint; set => SetProperty(ref _webhookEndpoint, value ?? string.Empty); }
    public bool WebhookUseHmac { get => _webhookUseHmac; set => SetProperty(ref _webhookUseHmac, value); }
    public string WebhookCredential { get => _webhookCredential; set => SetProperty(ref _webhookCredential, value ?? string.Empty); }
    public bool WebhookHasCredential { get => _webhookHasCredential; private set { if (SetProperty(ref _webhookHasCredential, value)) DeleteWebhookCredentialCommand.NotifyCanExecuteChanged(); } }
    public string WebhookStatus { get => _webhookStatus; private set => SetProperty(ref _webhookStatus, value); }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            ReadeckExportTarget? readeck = await _readeck.GetAsync(cancellationToken);
            OutlineExportTarget? outline = await _outline.GetAsync(cancellationToken);
            QBittorrentExportTarget? qbittorrent = await _qbittorrent.GetAsync(cancellationToken);
            WebhookExportTarget? webhook = await _webhook.GetAsync(cancellationToken);
            if (readeck is not null)
            {
                ReadeckEndpoint = readeck.Endpoint.AbsoluteUri;
                ReadeckArchive = readeck.Archive;
            }
            if (outline is not null)
            {
                OutlineEndpoint = outline.Endpoint.AbsoluteUri;
                OutlineCollectionId = outline.CollectionId.ToString("D");
            }
            if (qbittorrent is not null)
            {
                QBittorrentEndpoint = qbittorrent.Endpoint.AbsoluteUri;
                QBittorrentCategory = qbittorrent.Category;
            }
            if (webhook is not null)
            {
                WebhookEndpoint = webhook.Endpoint.AbsoluteUri;
                WebhookUseHmac = webhook.UseHmac;
            }
            ReadeckHasCredential = readeck?.CredentialVersion == 1
                && await HasCredentialAsync(EntryIntegrationKind.Readeck, cancellationToken);
            OutlineHasCredential = outline?.CredentialVersion == 1
                && await HasCredentialAsync(EntryIntegrationKind.Outline, cancellationToken);
            QBittorrentHasCredential = qbittorrent?.CredentialVersion == 1
                && await HasCredentialAsync(EntryIntegrationKind.QBittorrent, cancellationToken);
            WebhookHasCredential = webhook?.CredentialVersion == 1
                && await HasCredentialAsync(EntryIntegrationKind.Webhook, cancellationToken);
            ReadeckStatus = readeck is null ? "尚未保存 Readeck 目标。" : "已加载 Readeck 目标，token 未回读。";
            OutlineStatus = outline is null ? "尚未保存 Outline 目标。" : "已加载 Outline 目标，API key 未回读。";
            QBittorrentStatus = qbittorrent is null ? "尚未保存 qBittorrent 目标。" : "已加载 qBittorrent 目标，API key 未回读。";
            WebhookStatus = webhook is null ? "尚未保存 Webhook 目标。" : "已加载 Webhook 目标，HMAC secret 未回读。";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ReadeckStatus = OutlineStatus = QBittorrentStatus = WebhookStatus =
                "本机集成设置暂时无法读取。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveReadeckAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(
            async () =>
            {
                ReadeckExportTarget target = ReadeckExportTarget.Normalize(new(
                    ReadeckExportTarget.DefaultTargetId,
                    new Uri(ReadeckEndpoint, UriKind.Absolute),
                    ReadeckArchive,
                    CredentialVersion: 0));
                ReadeckExportTarget? current = await _readeck.GetAsync(
                    cancellationToken);
                bool hasNewCredential =
                    !string.IsNullOrWhiteSpace(ReadeckCredential);
                int credentialVersion = !hasNewCredential
                    && current?.CredentialVersion == 1
                    && SameEndpoint(current.Endpoint, target.Endpoint)
                        ? 1
                        : 0;
                if (hasNewCredential)
                {
                    await _readeck.SaveAsync(target, cancellationToken);
                    ReadeckHasCredential = false;
                    await SaveCredentialAsync(EntryIntegrationKind.Readeck, ReadeckCredential, cancellationToken);
                    credentialVersion = 1;
                }
                target = target with { CredentialVersion = credentialVersion };
                await _readeck.SaveAsync(target, cancellationToken);
                ReadeckHasCredential = credentialVersion == 1
                    && await HasCredentialAsync(EntryIntegrationKind.Readeck, cancellationToken);
                ReadeckStatus = ReadeckHasCredential ? "Readeck 目标与 token 已保存。" : "Readeck 目标已保存；尚未填写 token。";
            },
            value => ReadeckStatus = value,
            () => ReadeckCredential = string.Empty);
    }

    private async Task SaveOutlineAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(
            async () =>
            {
                OutlineExportTarget target = OutlineExportTarget.Normalize(new(
                    OutlineExportTarget.DefaultTargetId,
                    new Uri(OutlineEndpoint, UriKind.Absolute),
                    Guid.Parse(OutlineCollectionId),
                    CredentialVersion: 0));
                OutlineExportTarget? current = await _outline.GetAsync(
                    cancellationToken);
                bool hasNewCredential =
                    !string.IsNullOrWhiteSpace(OutlineCredential);
                int credentialVersion = !hasNewCredential
                    && current?.CredentialVersion == 1
                    && SameEndpoint(current.Endpoint, target.Endpoint)
                        ? 1
                        : 0;
                if (hasNewCredential)
                {
                    await _outline.SaveAsync(target, cancellationToken);
                    OutlineHasCredential = false;
                    await SaveCredentialAsync(EntryIntegrationKind.Outline, OutlineCredential, cancellationToken);
                    credentialVersion = 1;
                }
                target = target with { CredentialVersion = credentialVersion };
                await _outline.SaveAsync(target, cancellationToken);
                OutlineHasCredential = credentialVersion == 1
                    && await HasCredentialAsync(EntryIntegrationKind.Outline, cancellationToken);
                OutlineStatus = OutlineHasCredential ? "Outline 目标与 API key 已保存。" : "Outline 目标已保存；尚未填写 API key。";
            },
            value => OutlineStatus = value,
            () => OutlineCredential = string.Empty);
    }

    private async Task SaveQBittorrentAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(
            async () =>
            {
                QBittorrentExportTarget target = QBittorrentExportTarget.Normalize(new(
                    QBittorrentExportTarget.DefaultTargetId,
                    new Uri(QBittorrentEndpoint, UriKind.Absolute),
                    QBittorrentCategory,
                    CredentialVersion: 0));
                QBittorrentExportTarget? current = await _qbittorrent.GetAsync(
                    cancellationToken);
                bool hasNewCredential =
                    !string.IsNullOrWhiteSpace(QBittorrentCredential);
                int credentialVersion = !hasNewCredential
                    && current?.CredentialVersion == 1
                    && SameEndpoint(current.Endpoint, target.Endpoint)
                        ? 1
                        : 0;
                if (hasNewCredential)
                {
                    await _qbittorrent.SaveAsync(target, cancellationToken);
                    QBittorrentHasCredential = false;
                    await SaveCredentialAsync(EntryIntegrationKind.QBittorrent, QBittorrentCredential, cancellationToken);
                    credentialVersion = 1;
                }
                target = target with { CredentialVersion = credentialVersion };
                await _qbittorrent.SaveAsync(target, cancellationToken);
                QBittorrentHasCredential = credentialVersion == 1
                    && await HasCredentialAsync(EntryIntegrationKind.QBittorrent, cancellationToken);
                QBittorrentStatus = QBittorrentHasCredential ? "qBittorrent 目标与 API key 已保存。" : "qBittorrent 目标已保存；尚未填写 API key。";
            },
            value => QBittorrentStatus = value,
            () => QBittorrentCredential = string.Empty);
    }

    private async Task SaveWebhookAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(
            async () =>
            {
                WebhookExportTarget target = WebhookExportTarget.Normalize(new(
                    WebhookExportTarget.DefaultTargetId,
                    new Uri(WebhookEndpoint, UriKind.Absolute),
                    WebhookUseHmac,
                    CredentialVersion: 0));
                WebhookExportTarget? current = await _webhook.GetAsync(
                    cancellationToken);
                bool hasNewCredential = target.UseHmac
                    && !string.IsNullOrWhiteSpace(WebhookCredential);
                int credentialVersion = target.UseHmac
                    && !hasNewCredential
                    && current is { UseHmac: true, CredentialVersion: 1 }
                    && SameEndpoint(current.Endpoint, target.Endpoint)
                        ? 1
                        : 0;
                if (hasNewCredential)
                {
                    await _webhook.SaveAsync(target, cancellationToken);
                    WebhookHasCredential = false;
                    await SaveCredentialAsync(EntryIntegrationKind.Webhook, WebhookCredential, cancellationToken);
                    credentialVersion = 1;
                }
                target = target with { CredentialVersion = credentialVersion };
                await _webhook.SaveAsync(target, cancellationToken);
                WebhookHasCredential = credentialVersion == 1
                    && await HasCredentialAsync(EntryIntegrationKind.Webhook, cancellationToken);
                WebhookStatus = !WebhookUseHmac || WebhookHasCredential ? "Webhook 目标已保存。" : "Webhook 目标已保存；启用 HMAC 时还需 secret。";
            },
            value => WebhookStatus = value,
            () => WebhookCredential = string.Empty);
    }

    private Task TestReadeckAsync(CancellationToken cancellationToken) =>
        TestCredentialedAsync(_readeck, EntryIntegrationKind.Readeck, ReadeckEndpoint, value => ReadeckStatus = value, cancellationToken);
    private Task TestOutlineAsync(CancellationToken cancellationToken) =>
        TestCredentialedAsync(_outline, EntryIntegrationKind.Outline, OutlineEndpoint, value => OutlineStatus = value, cancellationToken);
    private Task TestQBittorrentAsync(CancellationToken cancellationToken) =>
        TestCredentialedAsync(_qbittorrent, EntryIntegrationKind.QBittorrent, QBittorrentEndpoint, value => QBittorrentStatus = value, cancellationToken);
    private async Task TestWebhookAsync(CancellationToken cancellationToken)
    {
        WebhookExportTarget? target = await _webhook.GetAsync(cancellationToken);
        if (target is null || target.UseHmac && target.CredentialVersion != 1)
        {
            WebhookStatus = "请先显式保存当前 Webhook 目标与所需凭据。";
            return;
        }
        if (target.UseHmac
            && !await HasCredentialAsync(
                EntryIntegrationKind.Webhook,
                cancellationToken))
        {
            WebhookHasCredential = false;
            WebhookStatus = "Webhook HMAC secret 已缺失；请重新填写并保存。";
            return;
        }
        WebhookExportTarget saved;
        WebhookExportTarget edited;
        try
        {
            saved = WebhookExportTarget.Normalize(target);
            edited = WebhookExportTarget.Normalize(target with
            {
                Endpoint = new Uri(WebhookEndpoint, UriKind.Absolute),
                UseHmac = WebhookUseHmac
            });
        }
        catch (Exception exception)
            when (exception is ArgumentException or UriFormatException)
        {
            WebhookStatus = "当前编辑值无效或尚未保存；未执行连接检查。";
            return;
        }
        if (!SameEndpoint(saved.Endpoint, edited.Endpoint)
            || saved.UseHmac != edited.UseHmac)
        {
            WebhookStatus = "当前编辑值尚未保存；未执行连接检查。";
            return;
        }
        await TestAsync(
            EntryIntegrationKind.Webhook,
            saved.Endpoint,
            value => WebhookStatus = value,
            cancellationToken);
    }

    private async Task TestCredentialedAsync<T>(
        IIntegrationExportTargetStore<T> store,
        EntryIntegrationKind kind,
        string endpoint,
        Action<string> setStatus,
        CancellationToken cancellationToken)
        where T : class
    {
        T? target = await store.GetAsync(cancellationToken);
        int credentialVersion = target switch
        {
            ReadeckExportTarget value => value.CredentialVersion,
            OutlineExportTarget value => value.CredentialVersion,
            QBittorrentExportTarget value => value.CredentialVersion,
            _ => 0
        };
        if (target is null || credentialVersion != 1)
        {
            setStatus("请先显式保存当前目标与新凭据；旧版占位凭据不会自动启用。");
            return;
        }
        Uri savedEndpoint;
        Uri editedEndpoint;
        try
        {
            savedEndpoint = GetSavedEndpoint(target);
            editedEndpoint = GetEditedEndpoint(target, endpoint);
        }
        catch (Exception exception)
            when (exception is ArgumentException or UriFormatException)
        {
            setStatus("当前地址无效或尚未保存；未执行连接检查。");
            return;
        }
        if (!SameEndpoint(savedEndpoint, editedEndpoint))
        {
            setStatus("当前地址尚未保存；未执行连接检查。");
            return;
        }
        await TestAsync(
            kind,
            savedEndpoint,
            setStatus,
            cancellationToken);
    }

    private async Task TestAsync(
        EntryIntegrationKind kind,
        Uri endpoint,
        Action<string> setStatus,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            EntryIntegrationHealthResult result = await _health.CheckAsync(
                new("default", kind, endpoint),
                cancellationToken);
            setStatus(result.Status == EntryIntegrationHealthStatus.Healthy
                ? "连接与能力检查通过。"
                : $"连接检查未通过：{result.Status}。");
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            setStatus("目标地址格式无效。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteCredentialAsync(
        EntryIntegrationKind kind,
        Func<CancellationToken, Task> deactivateTarget,
        Action<bool> setPresence,
        Action<string> setStatus,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            // The target marker is the activation authority. Persisting marker
            // zero first makes a crash or later out-of-band secret rewrite safe.
            await deactivateTarget(cancellationToken);
            await _credentials.DeleteAsync(kind, "default", cancellationToken);
            setPresence(false);
            setStatus("目标凭据已停用，并从 Windows DPAPI 存储删除。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync(
        Func<Task> action,
        Action<string> setStatus,
        Action clearCredential)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            setStatus("目标设置格式无效。");
        }
        catch (Exception exception)
            when (exception is InvalidOperationException
                or System.IO.IOException
                or System.Security.Cryptography.CryptographicException)
        {
            setStatus("本机目标或凭据暂时无法保存；未激活的凭据不会用于网络请求。");
        }
        finally
        {
            clearCredential();
            IsBusy = false;
        }
    }

    private Task<bool> HasCredentialAsync(
        EntryIntegrationKind kind,
        CancellationToken cancellationToken) =>
        _credentials.ExistsAsync(kind, "default", cancellationToken);

    private Task SaveCredentialAsync(
        EntryIntegrationKind kind,
        string value,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(value)
            ? Task.CompletedTask
            : _credentials.SetAsync(kind, "default", value.Trim(), cancellationToken);

    private static async Task DeactivateCredentialAsync<T>(
        IIntegrationExportTargetStore<T> store,
        Func<T, T> deactivate,
        CancellationToken cancellationToken)
        where T : class
    {
        T? target = await store.GetAsync(cancellationToken);
        if (target is not null)
        {
            await store.SaveAsync(deactivate(target), cancellationToken);
        }
    }

    private static Uri GetSavedEndpoint<T>(T target)
        where T : class =>
        target switch
        {
            ReadeckExportTarget value =>
                ReadeckExportTarget.Normalize(value).Endpoint,
            OutlineExportTarget value =>
                OutlineExportTarget.Normalize(value).Endpoint,
            QBittorrentExportTarget value =>
                QBittorrentExportTarget.Normalize(value).Endpoint,
            _ => throw new ArgumentException("不支持的集成目标。", nameof(target))
        };

    private static Uri GetEditedEndpoint<T>(T target, string endpoint)
        where T : class
    {
        var value = new Uri(endpoint, UriKind.Absolute);
        return target switch
        {
            ReadeckExportTarget current => ReadeckExportTarget.Normalize(
                current with { Endpoint = value }).Endpoint,
            OutlineExportTarget current => OutlineExportTarget.Normalize(
                current with { Endpoint = value }).Endpoint,
            QBittorrentExportTarget current => QBittorrentExportTarget.Normalize(
                current with { Endpoint = value }).Endpoint,
            _ => throw new ArgumentException("不支持的集成目标。", nameof(target))
        };
    }

    private bool CanOperate() => !IsBusy;

    private static bool SameEndpoint(Uri first, Uri second) =>
        string.Equals(
            first.AbsoluteUri,
            second.AbsoluteUri,
            StringComparison.Ordinal);

    private IEnumerable<AsyncRelayCommand> Commands()
    {
        yield return SaveReadeckCommand;
        yield return TestReadeckCommand;
        yield return DeleteReadeckCredentialCommand;
        yield return SaveOutlineCommand;
        yield return TestOutlineCommand;
        yield return DeleteOutlineCredentialCommand;
        yield return SaveQBittorrentCommand;
        yield return TestQBittorrentCommand;
        yield return DeleteQBittorrentCredentialCommand;
        yield return SaveWebhookCommand;
        yield return TestWebhookCommand;
        yield return DeleteWebhookCredentialCommand;
    }
}
