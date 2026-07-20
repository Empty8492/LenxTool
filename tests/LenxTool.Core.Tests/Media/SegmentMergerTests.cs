using LenxTool.Core.Media;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Media;

public sealed class SegmentMergerTests
{
    [Fact]
    public void MergeUsesHandoffBoundaryAndRemovesConsecutiveDuplicates()
    {
        SubtitleSegment[] existing =
        [
            Segment(0, 4, "欢迎使用 Lenx"),
            Segment(8, 12, "今天讨论人工智能")
        ];
        SubtitleSegment[] incoming =
        [
            Segment(8, 11, "今天   讨论人工智能"),
            Segment(11, 15, "以及它的实际应用")
        ];

        IReadOnlyList<SubtitleSegment> result = SegmentMerger.Merge(
            existing,
            incoming,
            TimeSpan.FromSeconds(10));

        Assert.Collection(
            result,
            item => Assert.Equal("欢迎使用 Lenx", item.Text),
            item => Assert.Equal("今天 讨论人工智能", item.Text),
            item => Assert.Equal("以及它的实际应用", item.Text));
        Assert.True(result[1].End <= result[2].Start);
    }

    [Fact]
    public void MergeFiltersHighProbabilityNonSpeech()
    {
        SubtitleSegment noise = Segment(0, 2, "背景噪声") with
        {
            NoSpeechProbability = 0.91,
            AverageLogProbability = -1.2
        };

        IReadOnlyList<SubtitleSegment> result = SegmentMerger.Merge(
            [],
            [noise, Segment(2, 4, "保留的人声")],
            TimeSpan.Zero);

        SubtitleSegment kept = Assert.Single(result);
        Assert.Equal("保留的人声", kept.Text);
    }

    private static SubtitleSegment Segment(double start, double end, string text) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);
}
