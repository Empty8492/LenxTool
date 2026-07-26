using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record HistorySearchTypeOption(
    ContentSearchResultType? Value,
    string Label);

public sealed record HistorySearchFilterOption(
    string? Id,
    string Label,
    string? CategoryId = null);
