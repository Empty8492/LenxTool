namespace LenxTool.Core.Models;

public sealed record SubtitleSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? TranslatedText = null,
    double? AverageLogProbability = null,
    double? NoSpeechProbability = null)
{
    public int? Sequence { get; init; }

    public double MidpointSeconds => (Start.TotalSeconds + End.TotalSeconds) / 2;
}
