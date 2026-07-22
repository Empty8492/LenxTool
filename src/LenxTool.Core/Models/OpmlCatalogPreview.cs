namespace LenxTool.Core.Models;

public enum OpmlCatalogItemStatus
{
    New,
    Duplicate,
    Conflict,
    Invalid
}

public sealed record OpmlCatalogPreviewItem(
    int Index,
    string Title,
    string FeedUrl,
    string? SiteUrl,
    string? CategoryName,
    string? CategoryId,
    OpmlCatalogItemStatus Status,
    string Message,
    bool IsSelected);
