namespace LenxTool.Core.Models;

public sealed record TrendSourceDefinition(
    string Id,
    string Name,
    string? ExpectedDomain);

public static class TrendSourceCatalog
{
    public static IReadOnlyList<TrendSourceDefinition> Default { get; } =
    [
        new("zhihu", "知乎", "zhihu.com"),
        new("douyin", "抖音", "douyin.com"),
        new("bilibili-hot-search", "bilibili 热搜", "bilibili.com"),
        new("wallstreetcn-hot", "华尔街见闻", "wallstreetcn.com"),
        new("tieba", "贴吧", "baidu.com"),
        new("baidu", "百度热搜", "baidu.com"),
        new("cls-hot", "财联社热门", "cls.cn"),
        new("thepaper", "澎湃新闻", "thepaper.cn"),
        new("ifeng", "凤凰网", "ifeng.com"),
        new("toutiao", "今日头条", "toutiao.com"),
        new("weibo", "微博", "weibo.com"),
        new("github-trending-today", "GitHub", "github.com"),
        new("hackernews", "Hacker News", null)
    ];
}
