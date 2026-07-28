using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record FeedPublishCategoryChoice(
    string? Id,
    string Label);

public sealed record FeedPublishViewChoice(
    FeedViewKind? Kind,
    string Label);

public sealed record FeedPublishFullTextChoice(
    FeedFullTextPolicy Policy,
    string Label);
