using LenxTool.Core.Media;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Media;

public sealed class SubtitleAndChunkTests
{
    [Fact]
    public void SrtCodecParsesMultilineAndExportsBilingualSubtitle()
    {
        const string source = "1\r\n00:00:01,250 --> 00:00:03,500\r\nHello\r\nworld\r\n\r\n";

        SubtitleSegment segment = Assert.Single(SrtCodec.Parse(source));
        string exported = SrtCodec.Export(
            [segment with { TranslatedText = "你好，世界" }],
            SubtitleExportMode.BilingualSrt);

        Assert.Equal("Hello\nworld", segment.Text);
        Assert.Contains("00:00:01,250 --> 00:00:03,500", exported, StringComparison.Ordinal);
        Assert.Contains("你好，世界\nHello\nworld", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkPlannerCreatesOverlappingCoverageForLongAudio()
    {
        IReadOnlyList<AudioChunk> chunks = AudioChunkPlanner.Plan(
            TimeSpan.FromMinutes(12),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(10));

        Assert.Equal(3, chunks.Count);
        Assert.Equal(TimeSpan.Zero, chunks[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(50), chunks[1].Start);
        Assert.Equal(TimeSpan.FromMinutes(12), chunks[^1].End);
        Assert.All(chunks, chunk => Assert.True(chunk.End > chunk.Start));
    }
}
