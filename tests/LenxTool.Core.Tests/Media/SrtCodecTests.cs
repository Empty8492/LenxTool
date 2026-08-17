using LenxTool.Core.Media;
using LenxTool.Core.Models;

namespace LenxTool.Core.Tests.Media;

public sealed class SrtCodecTests
{
    private static readonly SubtitleSegment[] Segments =
    [
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Hello")
        {
            Sequence = 7,
            TranslatedText = "你好"
        }
    ];

    [Fact]
    public void ExportTranslatedSrtKeepsOriginalSequenceAndTimeline()
    {
        string result = SrtCodec.Export(Segments, SubtitleExportMode.TranslatedSrt);

        Assert.Equal("7\n00:00:01,000 --> 00:00:02,000\n你好\n\n", result);
    }

    [Fact]
    public void ExportBilingualSrtWritesTranslationBeforeOriginal()
    {
        string result = SrtCodec.Export(Segments, SubtitleExportMode.BilingualSrt);

        Assert.Equal("7\n00:00:01,000 --> 00:00:02,000\n你好\nHello\n\n", result);
    }
}
