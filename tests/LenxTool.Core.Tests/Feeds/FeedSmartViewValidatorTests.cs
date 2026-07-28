using LenxTool.Core.Feeds;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Feeds;

public sealed class FeedSmartViewValidatorTests
{
    private const string ViewId =
        "30000000-0000-4000-8000-000000000001";
    private const string FeedId =
        "20000000-0000-4000-8000-000000000001";
    private const string CategoryId =
        "10000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidateNormalizesOnlyClosedFilterFields()
    {
        FeedSmartView normalized =
            FeedSmartViewValidator.ValidateAndNormalize(
                View());

        Assert.Equal("AI 视频 收藏", normalized.Name);
        Assert.Equal(FeedId, normalized.Filter.FeedId);
        Assert.Equal(CategoryId, normalized.Filter.CategoryId);
        Assert.Equal("release notes", normalized.Filter.SearchText);
        Assert.Equal(EntryViewKind.Video, normalized.Filter.ViewKind);
    }

    [Fact]
    public void ApplyCreatesLocalPrivateQueryWithoutMutatingDefinition()
    {
        FeedSmartView source = View();

        FeedEntryQuery query = FeedSmartViewValidator.Apply(
            source,
            Now,
            offset: 50,
            limit: 50,
            localProfile: "profile-a");

        Assert.Equal(FeedId, query.FeedId);
        Assert.Equal(CategoryId, query.CategoryId);
        Assert.Equal(EntryViewKind.Video, query.ViewKind);
        Assert.Equal(FeedEntryReadFilter.Unread, query.ReadFilter);
        Assert.True(query.FavoritesOnly);
        Assert.Equal(Now.AddDays(-30), query.PublishedFrom);
        Assert.True(query.ActiveOnly);
        Assert.False(query.IncludeHidden);
        Assert.Equal("profile-a", query.LocalProfile);
        Assert.Equal("  release   notes  ", source.Filter.SearchText);
    }

    [Theory]
    [InlineData("not-a-guid", null, 30)]
    [InlineData(null, "not-a-guid", 30)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, 366)]
    public void ValidateRejectsInvalidReferencesAndWindows(
        string? feedId,
        string? categoryId,
        int days)
    {
        FeedSmartView value = View() with
        {
            Filter = View().Filter with
            {
                FeedId = feedId,
                CategoryId = categoryId,
                PublishedWithinDays = days
            }
        };

        Assert.ThrowsAny<ArgumentException>(() =>
            FeedSmartViewValidator.ValidateAndNormalize(value));
    }

    [Fact]
    public void ValidateRejectsOversizedSearchAndDisabledApply()
    {
        FeedSmartView oversized = View() with
        {
            Filter = View().Filter with
            {
                SearchText = new string(
                    'x',
                    FeedSmartViewValidator.MaximumSearchLength + 1)
            }
        };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FeedSmartViewValidator.ValidateAndNormalize(oversized));
        Assert.Throws<InvalidOperationException>(() =>
            FeedSmartViewValidator.Apply(
                View() with { IsEnabled = false },
                Now,
                0,
                50));
    }

    private static FeedSmartView View() => new(
        ViewId,
        2,
        "  AI  视频 收藏  ",
        20,
        true,
        new(
            FeedId,
            CategoryId,
            EntryViewKind.Video,
            FeedEntryReadFilter.Unread,
            FavoritesOnly: true,
            "  release   notes  ",
            PublishedWithinDays: 30));
}
