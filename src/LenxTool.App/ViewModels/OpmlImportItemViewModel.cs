using LenxTool.App.Mvvm;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed class OpmlImportItemViewModel : ObservableObject
{
    private bool _isSelected;
    private OpmlCatalogItemStatus _status;
    private string _message;

    public OpmlImportItemViewModel(OpmlCatalogPreviewItem item)
    {
        Index = item.Index;
        Title = item.Title;
        FeedUrl = item.FeedUrl;
        SiteUrl = item.SiteUrl;
        CategoryName = item.CategoryName;
        CategoryId = item.CategoryId;
        _status = item.Status;
        _message = item.Message;
        _isSelected = item.IsSelected;
    }

    public int Index { get; }
    public string Title { get; private set; }
    public string FeedUrl { get; private set; }
    public string? SiteUrl { get; }
    public string? CategoryName { get; }
    public string? CategoryId { get; }
    public OpmlCatalogItemStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusLabel));
        }
    }
    public string StatusLabel => Status switch
    {
        OpmlCatalogItemStatus.New => "新增",
        OpmlCatalogItemStatus.Duplicate => "重复",
        OpmlCatalogItemStatus.Conflict => "冲突",
        OpmlCatalogItemStatus.Invalid => "无效",
        _ => "未知"
    };
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }
    public bool IsSelectable => Status == OpmlCatalogItemStatus.New;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, IsSelectable && value);
    }

    public void ApplyDiscovery(DiscoveredFeed feed)
    {
        FeedUrl = feed.FeedUrl;
        OnPropertyChanged(nameof(FeedUrl));
        Message = "安全验证通过，等待原子提交。";
    }

    public void RejectDiscovery(string message)
    {
        IsSelected = false;
        Status = OpmlCatalogItemStatus.Invalid;
        OnPropertyChanged(nameof(IsSelectable));
        Message = message;
    }
}
