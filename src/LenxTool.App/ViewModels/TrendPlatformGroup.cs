using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record TrendPlatformGroup(
    string Platform,
    IReadOnlyList<TrendItem> Items)
{
    public int Count => Items.Count;
}
