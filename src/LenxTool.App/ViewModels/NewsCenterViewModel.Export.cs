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
    private string _obsidianExportStatus =
        "仅在点击后检查 Obsidian 配置与管理员策略。";
    private string _eagleExportStatus =
        "仅对已验证图片提供显式 Eagle 导出。";

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

    private void ConfigureEntryExports(
        IEntryExportQueueService? entryExportQueueService,
        IEntryIntegrationPolicyService? entryIntegrationPolicyService,
        IObsidianExportTargetStore? obsidianExportTargetStore,
        IEagleExportTargetStore? eagleExportTargetStore,
        IEagleApiClient? eagleApiClient)
    {
        _entryExportQueueService = entryExportQueueService;
        _entryIntegrationPolicyService = entryIntegrationPolicyService;
        _obsidianExportTargetStore = obsidianExportTargetStore;
        _eagleExportTargetStore = eagleExportTargetStore;
        _eagleApiClient = eagleApiClient;
        ExportTimelineEntryToObsidianCommand = new(
            ExportTimelineEntryToObsidianAsync,
            CanExportTimelineEntryToObsidian);
        ExportTimelineEntryToEagleCommand = new(
            ExportTimelineEntryToEagleAsync,
            CanExportTimelineEntryToEagle);
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
    }
}
