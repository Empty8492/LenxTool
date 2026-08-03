using System.Globalization;
using System.Text;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Feeds;
using LenxTool.Core.Models;
using LenxTool.Infrastructure.Exports;
using LenxTool.Infrastructure.Networking;

namespace LenxTool.App.ViewModels;

public sealed partial class NewsCenterViewModel
{
    private IEntryExportQueueService? _entryExportQueueService;
    private IEntryIntegrationPolicyService? _entryIntegrationPolicyService;
    private IObsidianExportTargetStore? _obsidianExportTargetStore;
    private IEagleExportTargetStore? _eagleExportTargetStore;
    private IEagleApiClient? _eagleApiClient;
    private IZoteroExportTargetStore? _zoteroExportTargetStore;
    private IEntryIntegrationCredentialStore?
        _entryIntegrationCredentialStore;
    private string _obsidianExportStatus =
        "仅在点击后检查 Obsidian 配置与管理员策略。";
    private string _eagleExportStatus =
        "仅对已验证图片提供显式 Eagle 导出。";
    private string _zoteroExportStatus =
        "仅在显式点击后检查 Zotero 个人库、ACTIVE 策略与本机 API key。";
    private string _readwiseExportStatus =
        "Readwise 只发送下方可见的裁剪摘要；不会发送私人备注或完整正文。";

    public AsyncRelayCommand<FeedTimelineItem>
        ExportTimelineEntryToObsidianCommand
    {
        get;
        private set;
    } = null!;

    public string ObsidianExportStatus
    {
        get => _obsidianExportStatus;
        private set => SetProperty(ref _obsidianExportStatus, value);
    }

    public AsyncRelayCommand<FeedTimelineItem>
        ExportTimelineEntryToEagleCommand
    {
        get;
        private set;
    } = null!;

    public string EagleExportStatus
    {
        get => _eagleExportStatus;
        private set => SetProperty(ref _eagleExportStatus, value);
    }

    public AsyncRelayCommand<FeedTimelineItem>
        ExportTimelineEntryToZoteroCommand
    {
        get;
        private set;
    } = null!;

    public string ZoteroExportStatus
    {
        get => _zoteroExportStatus;
        private set => SetProperty(ref _zoteroExportStatus, value);
    }

    public AsyncRelayCommand<FeedTimelineItem>
        ExportTimelineEntryToReadwiseCommand
    {
        get;
        private set;
    } = null!;

    public string ReadwiseExportStatus
    {
        get => _readwiseExportStatus;
        private set => SetProperty(ref _readwiseExportStatus, value);
    }

    /// <summary>
    /// 与真正发送给 Reader `summary` 字段完全相同的有界预览；
    /// 选择条目不会读取凭据或发起网络请求。
    /// </summary>
    public string ReadwiseExportPreview =>
        SelectedTimelineEntry is { } item
            ? ReadwiseEntryExporter.CreateExcerptPreview(item.Entry).Text
            : string.Empty;

    private void ConfigureEntryExports(
        IEntryExportQueueService? entryExportQueueService,
        IEntryIntegrationPolicyService? entryIntegrationPolicyService,
        IObsidianExportTargetStore? obsidianExportTargetStore,
        IEagleExportTargetStore? eagleExportTargetStore,
        IEagleApiClient? eagleApiClient,
        IZoteroExportTargetStore? zoteroExportTargetStore,
        IEntryIntegrationCredentialStore?
            entryIntegrationCredentialStore)
    {
        _entryExportQueueService = entryExportQueueService;
        _entryIntegrationPolicyService = entryIntegrationPolicyService;
        _obsidianExportTargetStore = obsidianExportTargetStore;
        _eagleExportTargetStore = eagleExportTargetStore;
        _eagleApiClient = eagleApiClient;
        _zoteroExportTargetStore = zoteroExportTargetStore;
        _entryIntegrationCredentialStore =
            entryIntegrationCredentialStore;
        ExportTimelineEntryToObsidianCommand = new(
            ExportTimelineEntryToObsidianAsync,
            CanExportTimelineEntryToObsidian);
        ExportTimelineEntryToEagleCommand = new(
            ExportTimelineEntryToEagleAsync,
            CanExportTimelineEntryToEagle);
        ExportTimelineEntryToZoteroCommand = new(
            ExportTimelineEntryToZoteroAsync,
            CanExportTimelineEntryToZotero);
        ExportTimelineEntryToReadwiseCommand = new(
            ExportTimelineEntryToReadwiseAsync,
            CanExportTimelineEntryToReadwise);
    }

    private bool CanExportTimelineEntryToReadwise(
        FeedTimelineItem? item) =>
        item is not null
        && ReferenceEquals(item.Entry, SelectedTimelineEntry?.Entry)
        && _entryExportQueueService is not null
        && _entryIntegrationPolicyService is not null
        && _entryIntegrationCredentialStore is not null
        && ReadwiseEntryExporter.CanExportEntry(item.Entry);

    private async Task ExportTimelineEntryToReadwiseAsync(
        FeedTimelineItem? item,
        CancellationToken cancellationToken)
    {
        if (item is null
            || !ReferenceEquals(item.Entry, SelectedTimelineEntry?.Entry)
            || _entryExportQueueService is null
            || _entryIntegrationPolicyService is null
            || _entryIntegrationCredentialStore is null
            || !ReadwiseEntryExporter.CanExportEntry(item.Entry))
        {
            return;
        }

        // 编辑器加载可能只替换所选行的本机状态 record；真正导出始终重新取当前
        // 选中项，确保可见预览与发送正文来自同一个 FeedEntry 实例。
        item = SelectedTimelineEntry!;

        string targetLabel = GetExportTargetLabel(item);
        try
        {
            // 共享策略先于 DPAPI 槽位读取；停用或主机漂移时显式点击也不探测 token。
            EntryIntegrationPolicySnapshot snapshot =
                await _entryIntegrationPolicyService.GetAsync(
                    EntryIntegrationPolicyScope.Active,
                    cancellationToken);
            if (!snapshot.Policies.Any(policy =>
                    policy.Kind == EntryIntegrationKind.Readwise
                    && policy.IsEnabled
                    && policy.AllowedHosts.Contains(
                        ReadwiseEntryExporter.ApiRoot.IdnHost,
                        StringComparer.Ordinal)))
            {
                ReadwiseExportStatus =
                    $"条目“{targetLabel}”：管理员尚未启用 Readwise 或未允许 readwise.io，未加入导出队列。";
                return;
            }

            bool hasCredential =
                await _entryIntegrationCredentialStore.ExistsAsync(
                    EntryIntegrationKind.Readwise,
                    ReadwiseEntryExporter.CredentialTargetId,
                    cancellationToken);
            if (!hasCredential)
            {
                ReadwiseExportStatus =
                    $"条目“{targetLabel}”：请先在设置中保存 Readwise token。";
                return;
            }

            EntryExportRequest request = EntryExportRequest.Create(
                ReadwiseEntryExporter.ExporterId,
                ReadwiseEntryExporter.QueueTargetId,
                item.Entry,
                ClassifyExportView(item.Entry),
                ReadwiseEntryExporter.GetExportContentBytes(item.Entry));
            EntryExportEnqueueResult result =
                await _entryExportQueueService.EnqueueAsync(
                    request,
                    cancellationToken);
            ReadwiseExportStatus = result.Created
                ? $"条目“{targetLabel}”已加入 Readwise 导出队列。"
                : $"条目“{targetLabel}”的当前 Readwise 导出版本已存在于队列或历史中。";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // token、Reader 响应正文和未裁剪内容都不能通过页面状态回显。
            ReadwiseExportStatus =
                $"条目“{targetLabel}”：Readwise 导出暂时不可用，请稍后重试。";
        }
    }

    private bool CanExportTimelineEntryToZotero(
        FeedTimelineItem? item) =>
        item is not null
        && _entryExportQueueService is not null
        && _entryIntegrationPolicyService is not null
        && _zoteroExportTargetStore is not null
        && _entryIntegrationCredentialStore is not null;

    private async Task ExportTimelineEntryToZoteroAsync(
        FeedTimelineItem? item,
        CancellationToken cancellationToken)
    {
        if (item is null
            || _entryExportQueueService is null
            || _entryIntegrationPolicyService is null
            || _zoteroExportTargetStore is null
            || _entryIntegrationCredentialStore is null)
        {
            return;
        }

        string targetLabel = GetExportTargetLabel(item);
        try
        {
            // 入队只读取不含凭据的个人库目标；User ID 不进入状态文本或队列标识。
            ZoteroExportTarget? target =
                await _zoteroExportTargetStore.GetAsync(
                    cancellationToken);
            if (target is null
                || !string.Equals(
                    target.TargetId,
                    ZoteroExportTarget.DefaultTargetId,
                    StringComparison.Ordinal))
            {
                ZoteroExportStatus =
                    $"条目“{targetLabel}”：尚未配置 Zotero 个人库，未加入导出队列。";
                return;
            }

            // 管理策略优先于安全存储读取；禁用后显式点击也不能探测凭据或第三方。
            EntryIntegrationPolicySnapshot snapshot =
                await _entryIntegrationPolicyService.GetAsync(
                    EntryIntegrationPolicyScope.Active,
                    cancellationToken);
            if (!snapshot.Policies.Any(policy =>
                    policy.Kind == EntryIntegrationKind.Zotero
                    && policy.IsEnabled
                    && policy.AllowedHosts.Contains(
                        "api.zotero.org",
                        StringComparer.Ordinal)))
            {
                ZoteroExportStatus =
                    $"条目“{targetLabel}”：管理员尚未启用 Zotero 集成，未加入导出队列。";
                return;
            }

            bool hasCredential =
                await _entryIntegrationCredentialStore.ExistsAsync(
                    EntryIntegrationKind.Zotero,
                    ZoteroExportTarget.DefaultTargetId,
                    cancellationToken);
            if (!hasCredential)
            {
                ZoteroExportStatus =
                    $"条目“{targetLabel}”：请先在设置中保存 Zotero API key。";
                return;
            }

            EntryExportRequest request = EntryExportRequest.Create(
                "zotero",
                target.CreateQueueTargetId(),
                item.Entry,
                ClassifyExportView(item.Entry),
                GetZoteroExportContentBytes(item.Entry, target));
            EntryExportEnqueueResult result =
                await _entryExportQueueService.EnqueueAsync(
                    request,
                    cancellationToken);
            ZoteroExportStatus = result.Created
                ? $"条目“{targetLabel}”已加入 Zotero 导出队列。"
                : $"条目“{targetLabel}”的当前 Zotero 导出版本已存在于队列或历史中。";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 第三方正文、凭据与 User ID 都不得通过页面状态回显。
            ZoteroExportStatus =
                $"条目“{targetLabel}”：Zotero 导出暂时不可用，请稍后重试。";
        }
    }

    private static long GetZoteroExportContentBytes(
        FeedEntry entry,
        ZoteroExportTarget target)
    {
        long contentBytes = target.IncludeSummaryNote
            ? Encoding.UTF8.GetByteCount(entry.Summary)
            : 0;
        if (!target.UploadFirstImageAttachment)
        {
            return contentBytes;
        }

        // 首版 Zotero 与 Eagle 共用已经过 URL、声明类型和大小门控的图片判定；
        // 后台导出仍会重新下载并校验实际 MIME、魔数和字节数。
        FeedAttachmentClassification? attachment =
            EagleEntryExporter.SelectSupportedAttachment(entry);
        return checked(contentBytes + (attachment?.Length ?? 0));
    }

    private bool CanExportTimelineEntryToEagle(
        FeedTimelineItem? item) =>
        item is not null
        && _entryExportQueueService is not null
        && _entryIntegrationPolicyService is not null
        && _eagleExportTargetStore is not null
        && _eagleApiClient is not null
        && TryGetSupportedEaglePicture(
            item.Entry,
            out _);

    private async Task ExportTimelineEntryToEagleAsync(
        FeedTimelineItem? item,
        CancellationToken cancellationToken)
    {
        if (item is null
            || _entryExportQueueService is null
            || _entryIntegrationPolicyService is null
            || _eagleExportTargetStore is null
            || _eagleApiClient is null)
        {
            return;
        }

        string targetLabel = GetExportTargetLabel(item);
        try
        {
            // 入队与真正执行都重新校验目标作用域，防止端口切换后旧任务误投递。
            EagleExportTarget? target =
                await _eagleExportTargetStore.GetAsync(
                    cancellationToken);
            if (target is null
                || !string.Equals(
                    target.TargetId,
                    EagleExportTarget.DefaultTargetId,
                    StringComparison.Ordinal))
            {
                EagleExportStatus =
                    $"条目“{targetLabel}”：尚未配置本机 Eagle 端点，未加入导出队列。";
                return;
            }

            EntryIntegrationPolicySnapshot snapshot =
                await _entryIntegrationPolicyService.GetAsync(
                    EntryIntegrationPolicyScope.Active,
                    cancellationToken);
            bool isEnabled = snapshot.Policies.Any(
                policy =>
                    policy.Kind == EntryIntegrationKind.Eagle
                    && policy.IsEnabled);
            if (!isEnabled)
            {
                EagleExportStatus =
                    $"条目“{targetLabel}”：管理员尚未启用 Eagle 集成，未加入导出队列。";
                return;
            }

            if (!TryGetSupportedEaglePicture(
                    item.Entry,
                    out long declaredLength))
            {
                EagleExportStatus =
                    $"条目“{targetLabel}”：没有通过类型验证的图片附件。";
                return;
            }

            // 显式点击时读取当前资源库的不透明修订，使同端点切库后的任务拥有
            // 新的幂等作用域；名称和路径不会进入队列、界面或日志。
            EagleApiCapability capability = await _eagleApiClient.ProbeAsync(
                target.Endpoint,
                cancellationToken);

            EntryExportRequest request = EntryExportRequest.Create(
                EagleEntryExporter.ExporterId,
                target.CreateQueueTargetId(capability.LibraryRevision),
                item.Entry,
                EntryViewKind.Picture,
                declaredLength);
            EntryExportEnqueueResult result =
                await _entryExportQueueService.EnqueueAsync(
                    request,
                    cancellationToken);
            EagleExportStatus = result.Created
                ? $"条目“{targetLabel}”已加入 Eagle 导出队列。"
                : $"条目“{targetLabel}”的当前图片版本已存在于 Eagle 导出队列或历史中。";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 不把本机 Eagle 响应正文、端口之外的配置或图片地址回显到界面。
            EagleExportStatus =
                $"条目“{targetLabel}”：Eagle 导出暂时不可用，请稍后重试。";
        }
    }

    private static bool TryGetSupportedEaglePicture(
        FeedEntry entry,
        out long declaredLength)
    {
        FeedAttachmentClassification? classification =
            EagleEntryExporter.SelectSupportedAttachment(entry);
        if (classification is not null)
        {
            declaredLength = classification.Length ?? 0;
            return true;
        }

        declaredLength = 0;
        return false;
    }

    private bool CanExportTimelineEntryToObsidian(
        FeedTimelineItem? item) =>
        item is not null
        && _entryExportQueueService is not null
        && _entryIntegrationPolicyService is not null
        && _obsidianExportTargetStore is not null;

    private async Task ExportTimelineEntryToObsidianAsync(
        FeedTimelineItem? item,
        CancellationToken cancellationToken)
    {
        if (item is null
            || _entryExportQueueService is null
            || _entryIntegrationPolicyService is null
            || _obsidianExportTargetStore is null)
        {
            return;
        }

        string targetLabel = GetExportTargetLabel(item);
        try
        {
            // 每次显式点击都重新读取本机目标，避免使用过期设置。
            ObsidianExportTarget? target =
                await _obsidianExportTargetStore.GetAsync(
                    cancellationToken);
            if (target is null
                || !string.Equals(
                    target.TargetId,
                    ObsidianEntryExporter.TargetId,
                    StringComparison.Ordinal))
            {
                ObsidianExportStatus =
                    $"条目“{targetLabel}”：尚未配置 Obsidian Vault，未加入导出队列。";
                return;
            }

            // ACTIVE 管理策略是入队前的实时门禁，禁用时不创建任务。
            EntryIntegrationPolicySnapshot snapshot =
                await _entryIntegrationPolicyService.GetAsync(
                    EntryIntegrationPolicyScope.Active,
                    cancellationToken);
            bool isEnabled = snapshot.Policies.Any(
                policy =>
                    policy.Kind == EntryIntegrationKind.Obsidian
                    && policy.IsEnabled);
            if (!isEnabled)
            {
                ObsidianExportStatus =
                    $"条目“{targetLabel}”：管理员尚未启用 Obsidian 集成，未加入导出队列。";
                return;
            }

            EntryExportRequest request = EntryExportRequest.Create(
                ObsidianEntryExporter.ExporterId,
                target.CreateQueueTargetId(),
                item.Entry,
                ClassifyExportView(item.Entry),
                GetExportContentBytes(item.Entry));
            EntryExportEnqueueResult result =
                await _entryExportQueueService.EnqueueAsync(
                    request,
                    cancellationToken);
            ObsidianExportStatus = result.Created
                ? $"条目“{targetLabel}”已加入 Obsidian 导出队列。"
                : $"条目“{targetLabel}”的当前内容版本已存在于 Obsidian 导出队列或历史中。";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 界面只显示封闭错误，不泄露本机路径或第三方响应正文。
            ObsidianExportStatus =
                $"条目“{targetLabel}”：Obsidian 导出暂时不可用，请稍后重试。";
        }
    }

    private static string GetExportTargetLabel(
        FeedTimelineItem item)
    {
        string source = string.IsNullOrWhiteSpace(item.Entry.Title)
            ? item.FeedName
            : item.Entry.Title;
        var normalized = new StringBuilder(source.Length);
        bool pendingSpace = false;
        foreach (char value in source)
        {
            UnicodeCategory category =
                CharUnicodeInfo.GetUnicodeCategory(value);
            if (char.IsWhiteSpace(value)
                || char.IsControl(value)
                || category == UnicodeCategory.Format)
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }
            normalized.Append(value);
        }

        string label = normalized.ToString().Trim();
        if (label.Length == 0)
        {
            return "无标题条目";
        }

        const int MaximumTextElements = 48;
        var text = new StringInfo(label);
        return text.LengthInTextElements <= MaximumTextElements
            ? label
            : text.SubstringByTextElements(0, MaximumTextElements) + "…";
    }

    private EntryViewKind ClassifyExportView(
        FeedEntry entry)
    {
        FeedCatalogItem? feed = _timelineCatalog?.Feeds.FirstOrDefault(
            item => string.Equals(
                item.Id,
                entry.FeedId,
                StringComparison.Ordinal));
        EntryViewKind? explicitOverride =
            feed is { IsViewKindExplicit: true }
                ? MapExplicitViewKind(feed.ViewKind)
                : null;
        return EntryViewClassifier.Classify(
            explicitOverride,
            entry.Enclosures
                .Select(enclosure =>
                    FeedAttachmentClassifier.Classify(
                        enclosure,
                        entry.NormalizedUrl))
                .ToArray(),
            primaryContentMedia: null);
    }

    private static EntryViewKind MapExplicitViewKind(
        FeedViewKind viewKind) =>
        viewKind switch
        {
            FeedViewKind.Article => EntryViewKind.Article,
            FeedViewKind.Picture => EntryViewKind.Picture,
            FeedViewKind.Audio => EntryViewKind.Audio,
            FeedViewKind.Video => EntryViewKind.Video,
            FeedViewKind.Notification => EntryViewKind.Notification,
            _ => throw new ArgumentOutOfRangeException(
                nameof(viewKind),
                viewKind,
                "目录包含未知的 Feed 视图类型。")
        };

    private static long GetExportContentBytes(
        FeedEntry entry)
    {
        string content = string.IsNullOrWhiteSpace(
            entry.SanitizedContent)
            ? entry.Summary
            : entry.SanitizedContent;
        return Encoding.UTF8.GetByteCount(content);
    }

    private void DisposeEntryExports()
    {
        ExportTimelineEntryToObsidianCommand.Dispose();
        ExportTimelineEntryToEagleCommand.Dispose();
        ExportTimelineEntryToZoteroCommand.Dispose();
        ExportTimelineEntryToReadwiseCommand.Dispose();
    }
}
