using System.Collections.ObjectModel;
using LenxTool.App.Controls;
using LenxTool.App.Mvvm;
using LenxTool.Core.Contracts;
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

public sealed partial class NewsCenterViewModel
{
    private static readonly FeedReaderSourceOption RssReaderSource =
        new(FeedReaderContentSource.Rss, "RSS 正文");
    private static readonly FeedReaderSourceOption ExtractedReaderSource =
        new(FeedReaderContentSource.Extracted, "提取全文");

    private readonly IFeedFullTextQueueService _feedFullTextQueueService;
    private CancellationTokenSource? _feedReaderCancellation;
    private FeedFullTextContent? _selectedExtractedContent;
    private FeedReaderSourceOption _selectedFeedReaderSource = RssReaderSource;
    private RichArticleDocument? _selectedFeedArticleDocument;
    private DateTimeOffset? _feedReaderExtractedAt;
    private string _feedReaderStatus = "选择资讯后可查看正文来源。";
    private Task _selectedFeedReaderLoad = Task.CompletedTask;
    private int _feedReaderGeneration;

    public ObservableCollection<FeedReaderSourceOption> FeedReaderSourceOptions { get; } = [];

    public FeedReaderSourceOption SelectedFeedReaderSource
    {
        get => _selectedFeedReaderSource;
        set
        {
            FeedReaderSourceOption selected = FeedReaderSourceOptions.FirstOrDefault(
                    option => option.Source == value?.Source)
                ?? RssReaderSource;
            if (!SetProperty(ref _selectedFeedReaderSource, selected)) return;
            ApplySelectedFeedReaderSource();
            OnPropertyChanged(nameof(FeedReaderSourceLabel));
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

    public string FeedReaderSourceLabel => SelectedFeedReaderSource.Label;

    public string FeedReaderStatus
    {
        get => _feedReaderStatus;
        private set => SetProperty(ref _feedReaderStatus, value);
    }

    public Task SelectedFeedReaderLoad => _selectedFeedReaderLoad;

    public RelayCommand OpenSelectedFeedOriginalCommand { get; private set; } = null!;

    private void ConfigureFeedReader()
    {
        FeedReaderSourceOptions.Add(RssReaderSource);
        OpenSelectedFeedOriginalCommand = new(
            OpenSelectedFeedOriginal,
            CanOpenSelectedFeedOriginal);
    }

    private void SelectFeedReaderEntry(FeedTimelineItem? item)
    {
        _feedReaderCancellation?.Cancel();
        _feedReaderCancellation?.Dispose();
        _feedReaderCancellation = null;
        int generation = Interlocked.Increment(ref _feedReaderGeneration);

        _selectedExtractedContent = null;
        FeedReaderSourceOptions.Clear();
        FeedReaderSourceOptions.Add(RssReaderSource);
        _selectedFeedReaderSource = RssReaderSource;
        OnPropertyChanged(nameof(SelectedFeedReaderSource));
        OnPropertyChanged(nameof(FeedReaderSourceLabel));
        SelectedFeedArticle = item is null ? null : CreateReaderArticle(item);
        SelectedFeedArticleDocument = item is null ? null : CreateRssReaderDocument(item);
        FeedReaderExtractedAt = null;
        OpenSelectedFeedOriginalCommand.NotifyCanExecuteChanged();

        if (item is null)
        {
            FeedReaderStatus = "选择资讯后可查看正文来源。";
            _selectedFeedReaderLoad = Task.CompletedTask;
            OnPropertyChanged(nameof(SelectedFeedReaderLoad));
            return;
        }

        FeedReaderStatus = "正在检查可用的提取全文…";
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
            SelectedFeedArticleDocument = RichArticleFormatter.WithEnclosures(
                RichArticleFormatter.FromExtractedContent(extracted.Article),
                selected.Entry.Enclosures,
                selected.Entry.NormalizedUrl);
            FeedReaderExtractedAt = extracted.ExtractedAt;
            return;
        }

        SelectedFeedArticleDocument = SelectedTimelineEntry is { } item
            ? CreateRssReaderDocument(item)
            : null;
        FeedReaderExtractedAt = null;
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
        _feedReaderCancellation?.Cancel();
        _feedReaderCancellation?.Dispose();
        _feedReaderCancellation = null;
    }
}
