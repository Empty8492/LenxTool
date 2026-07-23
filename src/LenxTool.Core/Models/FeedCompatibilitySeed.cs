namespace LenxTool.Core.Models;

public static class FeedCompatibilitySeed
{
    public const string Url = "https://daily.juya.uk/rss.xml";
    public const string DisplayName = "每日早报";

    public static FeedCatalogItemInput CreateInput(
        string? categoryId = null,
        int sortOrder = 1) => new(
            Url,
            DisplayName,
            "https://daily.juya.uk/",
            categoryId,
            FeedViewKind.Article,
            60,
            sortOrder,
            true);
}
