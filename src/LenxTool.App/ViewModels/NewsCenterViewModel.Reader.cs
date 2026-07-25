using System.Collections.ObjectModel;
using LenxTool.App.Controls;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public enum FeedReaderContentSource
{
    Rss,
    Extracted
}

public sealed record FeedReaderSourceOption(
    FeedReaderContentSource Source,
    string Label);

public enum FeedReaderLanguageMode
{
    Original,
    Translation,
    Bilingual
}

public sealed record FeedReaderLanguageOption(
    FeedReaderLanguageMode Mode,
    string Label);

public sealed partial class NewsCenterViewModel
{
    private static readonly FeedReaderSourceOption RssReaderSource =
        new(FeedReaderContentSource.Rss, "RSS 正文");
    private static readonly FeedReaderSourceOption ExtractedReaderSource =
        new(FeedReaderContentSource.Extracted, "提取全文");
    private static readonly FeedReaderLanguageOption OriginalReaderLanguage =
        new(FeedReaderLanguageMode.Original, "原文");
    private static readonly FeedReaderLanguageOption TranslationReaderLanguage =
        new(FeedReaderLanguageMode.Translation, "译文");
    private static readonly FeedReaderLanguageOption BilingualReaderLanguage =
        new(FeedReaderLanguageMode.Bilingual, "双语");

    private readonly IFeedFullTextQueueService _feedFullTextQueueService;
    private readonly IFeedAiSummaryService _feedAiSummaryService;
    private readonly IFeedAiTranslationService _feedAiTranslationService;
    private readonly Dictionary<string, FeedAiResult> _feedSummariesByEntryId =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? _feedReaderCancellation;
    private FeedFullTextContent? _selectedExtractedContent;
    private FeedReaderSourceOption _selectedFeedReaderSource = RssReaderSource;
    private FeedReaderLanguageOption _selectedFeedReaderLanguage = OriginalReaderLanguage;
    private RichArticleDocument? _selectedFeedSourceDocument;
    private RichArticleTranslationSource? _selectedFeedTranslationSource;
    private RichArticleDocument? _selectedFeedArticleDocument;
    private FeedAiResult? _selectedFeedSummary;
    private FeedAiTranslationResult? _selectedFeedTranslation;
    private AppError? _feedSummaryError;
    private AppError? _feedTranslationError;
    private DateTimeOffset? _feedReaderExtractedAt;
    private string _feedReaderStatus = "选择资讯后可查看正文来源。";
    private string _feedSummaryStatus = "选择资讯后可生成摘要。";
    private string _feedBatchSummaryStatus = "可摘要当前已加载列表的前 20 条资讯。";
    private string _feedTranslationStatus = "选择资讯后可生成本地缓存译文。";
    private string _selectedFeedTranslationTargetLanguage = "简体中文";
    private Task _selectedFeedReaderLoad = Task.CompletedTask;
    private int _feedReaderGeneration;
    private int _feedTranslationGeneration;

    public ObservableCollection<FeedReaderSourceOption> FeedReaderSourceOptions { get; } = [];
    public ObservableCollection<FeedReaderLanguageOption> FeedReaderLanguageOptions { get; } = [];
    public ObservableCollection<string> FeedTranslationTargetLanguages { get; } =
        ["简体中文", "English", "日本語", "한국어"];

    public FeedReaderSourceOption SelectedFeedReaderSource
    {
        get => _selectedFeedReaderSource;
        set
        {
            FeedReaderSourceOption selected = FeedReaderSourceOptions.FirstOrDefault(
                    option => option.Source == value?.Source)
                ?? RssReaderSource;
            if (!SetProperty(ref _selectedFeedReaderSource, selected)) return;
            GenerateFeedTranslationCommand.Cancel();
            ResetSelectedFeedTranslation(
                $"可将{selected.Label}翻译为{SelectedFeedTranslationTargetLanguage}。");
            ApplySelectedFeedReaderSource();
            ApplyStoredFeedSummary();
            OnPropertyChanged(nameof(FeedReaderSourceLabel));
        }
    }

    public FeedReaderLanguageOption SelectedFeedReaderLanguage
    {
        get => _selectedFeedReaderLanguage;
        set
        {
            FeedReaderLanguageOption selected = FeedReaderLanguageOptions.FirstOrDefault(
                    option => option.Mode == value?.Mode)
                ?? OriginalReaderLanguage;
            if (!SetProperty(ref _selectedFeedReaderLanguage, selected)) return;
            ApplySelectedFeedReaderLanguage();
            OnPropertyChanged(nameof(FeedReaderSourceLabel));
        }
    }

    public string SelectedFeedTranslationTargetLanguage
    {
        get => _selectedFeedTranslationTargetLanguage;
        set
        {
            string selected = FeedTranslationTargetLanguages.FirstOrDefault(
                    language => string.Equals(language, value, StringComparison.Ordinal))
                ?? "简体中文";
            if (!SetProperty(ref _selectedFeedTranslationTargetLanguage, selected)) return;
            GenerateFeedTranslationCommand.Cancel();
            ResetSelectedFeedTranslation(
                SelectedTimelineEntry is null
                    ? "选择资讯后可生成本地缓存译文。"
                    : $"可将{SelectedFeedReaderSource.Label}翻译为{selected}。");
        }
    }

    public RichArticleDocument? SelectedFeedArticleDocument
    {
        get => _selectedFeedArticleDocument;
        private set => SetProperty(ref _selectedFeedArticleDocument, value);
    }

    public DateTimeOffset? FeedReaderExtractedAt
    {
        get => _feedReaderExtractedAt;
        private set => SetProperty(ref _feedReaderExtractedAt, value);
    }

    public string FeedReaderSourceLabel =>
        SelectedFeedReaderLanguage.Mode == FeedReaderLanguageMode.Original
            ? SelectedFeedReaderSource.Label
            : $"{SelectedFeedReaderSource.Label} · {SelectedFeedReaderLanguage.Label}";

    public FeedAiResult? SelectedFeedSummary
    {
        get => _selectedFeedSummary;
        private set
        {
            if (SetProperty(ref _selectedFeedSummary, value))
            {
                OnPropertyChanged(nameof(FeedSummaryMeta));
            }
        }
    }

    public string FeedSummaryMeta =>
        SelectedFeedSummary is null
            ? string.Empty
            : $"{SelectedFeedSummary.CacheKey.Model} · {SelectedFeedSummary.TotalTokens} tokens · " +
              $"{SelectedFeedSummary.UpdatedAt.ToLocalTime():MM-dd HH:mm}";

    public AppError? FeedSummaryError
    {
        get => _feedSummaryError;
        private set => SetProperty(ref _feedSummaryError, value);
    }

    public FeedAiTranslationResult? SelectedFeedTranslation
    {
        get => _selectedFeedTranslation;
        private set
        {
            if (SetProperty(ref _selectedFeedTranslation, value))
            {
                OnPropertyChanged(nameof(FeedTranslationMeta));
            }
        }
    }

    public string FeedTranslationMeta =>
        SelectedFeedTranslation is null
            ? string.Empty
            : $"{SelectedFeedTranslation.CacheRecord.CacheKey.TargetLanguage} · " +
              $"{SelectedFeedTranslation.CacheRecord.CacheKey.Model} · " +
              $"{SelectedFeedTranslation.CacheRecord.TotalTokens} tokens";

    public AppError? FeedTranslationError
    {
        get => _feedTranslationError;
        private set => SetProperty(ref _feedTranslationError, value);
    }

    public string FeedReaderStatus
    {
        get => _feedReaderStatus;
        private set => SetProperty(ref _feedReaderStatus, value);
    }

    public Task SelectedFeedReaderLoad => _selectedFeedReaderLoad;

    public string FeedSummaryStatus
    {
        get => _feedSummaryStatus;
        private set => SetProperty(ref _feedSummaryStatus, value);
    }

    public string FeedBatchSummaryStatus
    {
        get => _feedBatchSummaryStatus;
        private set => SetProperty(ref _feedBatchSummaryStatus, value);
    }

    public string FeedTranslationStatus
    {
        get => _feedTranslationStatus;
        private set => SetProperty(ref _feedTranslationStatus, value);
    }

    public RelayCommand OpenSelectedFeedOriginalCommand { get; private set; } = null!;
    public AsyncRelayCommand GenerateFeedSummaryCommand { get; private set; } = null!;
    public AsyncRelayCommand GenerateVisibleFeedSummariesCommand { get; private set; } = null!;
    public AsyncRelayCommand GenerateFeedTranslationCommand { get; private set; } = null!;

    private void ConfigureFeedReader()
    {
        FeedReaderSourceOptions.Add(RssReaderSource);
        FeedReaderLanguageOptions.Add(OriginalReaderLanguage);
        OpenSelectedFeedOriginalCommand = new(
            OpenSelectedFeedOriginal,
            CanOpenSelectedFeedOriginal);
        GenerateFeedSummaryCommand = new(
            GenerateSelectedFeedSummaryAsync,
            () => SelectedTimelineEntry is not null);
        GenerateVisibleFeedSummariesCommand = new(
            GenerateVisibleFeedSummariesAsync,
            () => TimelineEntries.Count > 0);
        GenerateFeedTranslationCommand = new(
            GenerateSelectedFeedTranslationAsync,
            () => SelectedTimelineEntry is not null);
    }

    private void SelectFeedReaderEntry(FeedTimelineItem? item)
    {
        GenerateFeedSummaryCommand.Cancel();
        GenerateFeedTranslationCommand.Cancel();
        _feedReaderCancellation?.Cancel();
        _feedReaderCancellation?.Dispose();
        _feedReaderCancellation = null;
        int generation = Interlocked.Increment(ref _feedReaderGeneration);

        _selectedExtractedContent = null;
        _selectedFeedSourceDocument = null;
        FeedReaderSourceOptions.Clear();
        FeedReaderSourceOptions.Add(RssReaderSource);
        _selectedFeedReaderSource = RssReaderSource;
        OnPropertyChanged(nameof(SelectedFeedReaderSource));
        OnPropertyChanged(nameof(FeedReaderSourceLabel));
        SelectedFeedArticle = item is null ? null : CreateReaderArticle(item);
        _selectedFeedSourceDocument = item is null ? null : CreateRssReaderDocument(item);
        SelectedFeedArticleDocument = _selectedFeedSourceDocument;
        FeedReaderExtractedAt = null;
        FeedSummaryError = null;
        SelectedFeedSummary = null;
        ResetSelectedFeedTranslation(
            item is null
                ? "选择资讯后可生成本地缓存译文。"
                : $"可将 RSS 正文翻译为{SelectedFeedTranslationTargetLanguage}。");
        OpenSelectedFeedOriginalCommand.NotifyCanExecuteChanged();
        GenerateFeedSummaryCommand.NotifyCanExecuteChanged();
        GenerateFeedTranslationCommand.NotifyCanExecuteChanged();

        if (item is null)
        {
            FeedReaderStatus = "选择资讯后可查看正文来源。";
            FeedSummaryStatus = "选择资讯后可生成摘要。";
            _selectedFeedReaderLoad = Task.CompletedTask;
            OnPropertyChanged(nameof(SelectedFeedReaderLoad));
            return;
        }

        FeedReaderStatus = "正在检查可用的提取全文…";
        ApplyStoredFeedSummary();
        var cancellation = new CancellationTokenSource();
        _feedReaderCancellation = cancellation;
        _selectedFeedReaderLoad = LoadSelectedFeedFullTextAsync(
            item,
            generation,
            cancellation.Token);
        OnPropertyChanged(nameof(SelectedFeedReaderLoad));
    }

    private async Task LoadSelectedFeedFullTextAsync(
        FeedTimelineItem item,
        int expectedGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            FeedFullTextContent? content = await _feedFullTextQueueService
                .FetchOnOpenAsync(item.Entry.Id, cancellationToken);
            if (!IsCurrentFeedReaderEntry(item, expectedGeneration)) return;
            if (content is null)
            {
                FeedReaderStatus = item.Entry.HasFullContent
                    ? "RSS 已包含完整正文。"
                    : "当前 Feed 未提供提取全文，正在显示 RSS 正文。";
                return;
            }

            _selectedExtractedContent = content;
            FeedReaderSourceOptions.Add(ExtractedReaderSource);
            SelectedFeedReaderSource = ExtractedReaderSource;
            FeedReaderStatus = $"全文提取于 {content.ExtractedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer selection owns the reader now.
        }
        catch (Exception)
        {
            if (IsCurrentFeedReaderEntry(item, expectedGeneration))
            {
                FeedReaderStatus = "提取全文暂不可用，正在显示 RSS 正文。";
            }
        }
    }

    private void ApplySelectedFeedReaderSource()
    {
        if (SelectedFeedReaderSource.Source == FeedReaderContentSource.Extracted
            && _selectedExtractedContent is { } extracted
            && SelectedTimelineEntry is { } selected)
        {
            _selectedFeedSourceDocument = RichArticleFormatter.WithEnclosures(
                RichArticleFormatter.FromExtractedContent(extracted.Article),
                selected.Entry.Enclosures,
                selected.Entry.NormalizedUrl);
            FeedReaderExtractedAt = extracted.ExtractedAt;
        }
        else
        {
            _selectedFeedSourceDocument = SelectedTimelineEntry is { } item
                ? CreateRssReaderDocument(item)
                : null;
            FeedReaderExtractedAt = null;
        }
        ApplySelectedFeedReaderLanguage();
    }

    private void ApplySelectedFeedReaderLanguage()
    {
        if (SelectedFeedReaderLanguage.Mode == FeedReaderLanguageMode.Original
            || SelectedFeedTranslation is null
            || _selectedFeedTranslationSource is null)
        {
            SelectedFeedArticleDocument = _selectedFeedSourceDocument;
            return;
        }

        SelectedFeedArticleDocument = RichArticleFormatter.ApplyTranslation(
            _selectedFeedTranslationSource,
            SelectedFeedTranslation,
            SelectedFeedReaderLanguage.Mode == FeedReaderLanguageMode.Bilingual);
    }

    private async Task GenerateSelectedFeedSummaryAsync(CancellationToken cancellationToken)
    {
        FeedAiSummaryInput? input = CreateCurrentFeedSummaryInput();
        FeedTimelineItem? selected = SelectedTimelineEntry;
        if (input is null || selected is null) return;
        int expectedGeneration = Volatile.Read(ref _feedReaderGeneration);
        FeedSummaryError = null;
        FeedSummaryStatus = $"正在基于{SelectedFeedReaderSource.Label}生成摘要…";
        try
        {
            FeedAiResult result = await _feedAiSummaryService
                .SummarizeAsync(input, cancellationToken);
            if (!IsCurrentFeedReaderEntry(selected, expectedGeneration)
                || !string.Equals(
                    CreateCurrentFeedSummaryInput()?.ContentHash,
                    input.ContentHash,
                    StringComparison.Ordinal))
            {
                return;
            }

            _feedSummariesByEntryId[result.CacheKey.EntryId] = result;
            SelectedFeedSummary = result;
            FeedSummaryStatus = $"摘要已生成 · {result.TotalTokens} tokens";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentFeedReaderEntry(selected, expectedGeneration))
            {
                FeedSummaryStatus = "摘要生成已取消。";
            }
        }
        catch (AppException exception)
        {
            if (IsCurrentFeedReaderEntry(selected, expectedGeneration))
            {
                FeedSummaryError = exception.Error;
                FeedSummaryStatus =
                    $"{exception.Error.UserMessage} {exception.Error.Suggestion}";
            }
        }
    }

    private async Task GenerateVisibleFeedSummariesAsync(CancellationToken cancellationToken)
    {
        int timelineGeneration = Volatile.Read(ref _timelineQueryGeneration);
        FeedAiSummaryInput[] inputs = TimelineEntries
            .Take(20)
            .Select(item => CreateRssFeedSummaryInput(item.Entry))
            .ToArray();
        if (inputs.Length == 0) return;

        FeedBatchSummaryStatus = $"正在摘要前 {inputs.Length} 条资讯…";
        IReadOnlyList<FeedAiSummaryBatchItem> results = await _feedAiSummaryService
            .SummarizeBatchAsync(inputs, cancellationToken);
        if (timelineGeneration != Volatile.Read(ref _timelineQueryGeneration)) return;

        int completed = 0;
        int failed = 0;
        foreach (FeedAiSummaryBatchItem item in results)
        {
            if (item.Result is { } result)
            {
                _feedSummariesByEntryId[result.CacheKey.EntryId] = result;
                completed++;
            }
            else if (item.Error is not null)
            {
                failed++;
            }
        }
        ApplyStoredFeedSummary();
        FeedBatchSummaryStatus = failed == 0
            ? $"当前页摘要已完成 {completed}/{inputs.Length}。"
            : $"当前页摘要完成 {completed}/{inputs.Length}，失败 {failed} 条；可稍后重试。";
    }

    private async Task GenerateSelectedFeedTranslationAsync(
        CancellationToken cancellationToken)
    {
        FeedTranslationRequestContext? context = CreateCurrentFeedTranslationContext();
        FeedTimelineItem? selected = SelectedTimelineEntry;
        if (context is null || selected is null) return;
        int expectedReaderGeneration = Volatile.Read(ref _feedReaderGeneration);
        int expectedTranslationGeneration = Volatile.Read(ref _feedTranslationGeneration);
        FeedTranslationError = null;
        FeedTranslationStatus =
            $"正在将{SelectedFeedReaderSource.Label}翻译为{context.Input.TargetLanguage}…";
        try
        {
            FeedAiTranslationResult result = await _feedAiTranslationService
                .TranslateAsync(context.Input, cancellationToken);
            if (!IsCurrentFeedReaderEntry(selected, expectedReaderGeneration)
                || expectedTranslationGeneration != Volatile.Read(ref _feedTranslationGeneration))
            {
                return;
            }

            _selectedFeedTranslationSource = context.Source;
            SelectedFeedTranslation = result;
            EnsureFeedReaderLanguageOptions();
            SelectedFeedReaderLanguage = TranslationReaderLanguage;
            FeedTranslationStatus =
                $"译文已就绪 · {result.CacheRecord.TotalTokens} tokens · 已保存到本机";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentFeedReaderEntry(selected, expectedReaderGeneration)
                && expectedTranslationGeneration ==
                    Volatile.Read(ref _feedTranslationGeneration))
            {
                FeedTranslationStatus = "翻译已取消；已完成批次可在下次继续。";
            }
        }
        catch (AppException exception)
        {
            if (IsCurrentFeedReaderEntry(selected, expectedReaderGeneration)
                && expectedTranslationGeneration ==
                    Volatile.Read(ref _feedTranslationGeneration))
            {
                FeedTranslationError = exception.Error;
                FeedTranslationStatus =
                    $"{exception.Error.UserMessage} {exception.Error.Suggestion}";
                ApplySelectedFeedReaderLanguage();
            }
        }
    }

    private FeedAiSummaryInput? CreateCurrentFeedSummaryInput()
    {
        if (SelectedTimelineEntry is not { } selected) return null;
        if (SelectedFeedReaderSource.Source == FeedReaderContentSource.Extracted
            && _selectedExtractedContent is { } extracted)
        {
            string content = string.Join(
                Environment.NewLine,
                extracted.Article.Blocks
                    .Where(block => !string.IsNullOrWhiteSpace(block.Text))
                    .Select(block => block.Text));
            return new(
                selected.Entry.Id,
                extracted.ContentHash,
                selected.Entry.Title,
                content);
        }
        return CreateRssFeedSummaryInput(selected.Entry);
    }

    private static FeedAiSummaryInput CreateRssFeedSummaryInput(FeedEntry entry)
    {
        string content = string.IsNullOrWhiteSpace(entry.SanitizedContent)
            ? entry.Summary
            : entry.SanitizedContent;
        return new(entry.Id, entry.ContentHash, entry.Title, content);
    }

    private void ApplyStoredFeedSummary()
    {
        FeedAiSummaryInput? input = CreateCurrentFeedSummaryInput();
        if (input is not null
            && _feedSummariesByEntryId.TryGetValue(input.EntryId, out FeedAiResult? result)
            && string.Equals(
                result.CacheKey.ContentHash,
                input.ContentHash,
                StringComparison.Ordinal))
        {
            SelectedFeedSummary = result;
            FeedSummaryStatus = $"已加载{SelectedFeedReaderSource.Label}摘要。";
            return;
        }

        SelectedFeedSummary = null;
        FeedSummaryError = null;
        FeedSummaryStatus = SelectedTimelineEntry is null
            ? "选择资讯后可生成摘要。"
            : $"可基于{SelectedFeedReaderSource.Label}生成摘要。";
    }

    private static RichArticleDocument? CreateRssReaderDocument(FeedTimelineItem item)
    {
        if (item.Entry.Enclosures.Count == 0) return null;
        string content = string.IsNullOrWhiteSpace(item.Entry.SanitizedContent)
            ? item.Entry.Summary
            : item.Entry.SanitizedContent;
        return RichArticleFormatter.WithEnclosures(
            RichArticleFormatter.Parse(content, item.Entry.NormalizedUrl),
            item.Entry.Enclosures,
            item.Entry.NormalizedUrl);
    }

    private FeedTranslationRequestContext? CreateCurrentFeedTranslationContext()
    {
        if (SelectedTimelineEntry is not { } selected
            || SelectedFeedArticle is null)
        {
            return null;
        }

        RichArticleDocument document = _selectedFeedSourceDocument
            ?? RichArticleFormatter.Parse(
                string.IsNullOrWhiteSpace(SelectedFeedArticle.RichContent)
                    ? SelectedFeedArticle.Content
                    : SelectedFeedArticle.RichContent,
                SelectedFeedArticle.Url);
        RichArticleTranslationSource source = RichArticleFormatter.CreateTranslationSource(
            document,
            selected.Entry.Title);
        string contentHash =
            SelectedFeedReaderSource.Source == FeedReaderContentSource.Extracted
            && _selectedExtractedContent is { } extracted
                ? extracted.ContentHash
                : selected.Entry.ContentHash;
        return new(
            source,
            new(
                selected.Entry.Id,
                contentHash,
                selected.Entry.Title,
                SelectedFeedTranslationTargetLanguage,
                source.Blocks));
    }

    private void EnsureFeedReaderLanguageOptions()
    {
        if (!FeedReaderLanguageOptions.Contains(TranslationReaderLanguage))
            FeedReaderLanguageOptions.Add(TranslationReaderLanguage);
        if (!FeedReaderLanguageOptions.Contains(BilingualReaderLanguage))
            FeedReaderLanguageOptions.Add(BilingualReaderLanguage);
    }

    private void ResetSelectedFeedTranslation(string status)
    {
        Interlocked.Increment(ref _feedTranslationGeneration);
        _selectedFeedTranslationSource = null;
        SelectedFeedTranslation = null;
        FeedTranslationError = null;
        FeedReaderLanguageOptions.Clear();
        FeedReaderLanguageOptions.Add(OriginalReaderLanguage);
        if (!Equals(_selectedFeedReaderLanguage, OriginalReaderLanguage))
        {
            _selectedFeedReaderLanguage = OriginalReaderLanguage;
            OnPropertyChanged(nameof(SelectedFeedReaderLanguage));
            OnPropertyChanged(nameof(FeedReaderSourceLabel));
        }
        FeedTranslationStatus = status;
        ApplySelectedFeedReaderLanguage();
    }

    private bool CanOpenSelectedFeedOriginal() =>
        TryGetSelectedFeedOriginalUri(out _);

    private void OpenSelectedFeedOriginal()
    {
        if (TryGetSelectedFeedOriginalUri(out Uri? uri) && uri is not null)
        {
            _dialogs.OpenUri(uri.AbsoluteUri);
        }
    }

    private bool TryGetSelectedFeedOriginalUri(out Uri? uri)
    {
        string? value = SelectedTimelineEntry?.Entry.NormalizedUrl;
        bool isSafe = Uri.TryCreate(value, UriKind.Absolute, out uri)
            && uri.Scheme is "http" or "https"
            && string.IsNullOrEmpty(uri.UserInfo);
        if (!isSafe) uri = null;
        return isSafe;
    }

    private bool IsCurrentFeedReaderEntry(
        FeedTimelineItem item,
        int expectedGeneration) =>
        expectedGeneration == Volatile.Read(ref _feedReaderGeneration)
        && string.Equals(
            SelectedTimelineEntry?.Entry.Id,
            item.Entry.Id,
            StringComparison.Ordinal);

    private void DisposeFeedReader()
    {
        Interlocked.Increment(ref _feedReaderGeneration);
        GenerateFeedSummaryCommand.Dispose();
        GenerateVisibleFeedSummariesCommand.Dispose();
        GenerateFeedTranslationCommand.Dispose();
        _feedReaderCancellation?.Cancel();
        _feedReaderCancellation?.Dispose();
        _feedReaderCancellation = null;
    }

    private sealed record FeedTranslationRequestContext(
        RichArticleTranslationSource Source,
        FeedAiTranslationInput Input);
}
