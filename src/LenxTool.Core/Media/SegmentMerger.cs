using System.Text.RegularExpressions;
using LenxTool.Core.Models;

namespace LenxTool.Core.Media;

public static partial class SegmentMerger
{
    public static IReadOnlyList<SubtitleSegment> Merge(
        IEnumerable<SubtitleSegment> existing,
        IEnumerable<SubtitleSegment> incoming,
        TimeSpan handoff)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        IEnumerable<SubtitleSegment> candidates = existing
            .Where(segment => segment.End <= handoff)
            .Concat(incoming.Where(segment => segment.End > handoff))
            .Where(IsSpeech)
            .Select(Normalize)
            .Where(segment => segment.End > segment.Start && segment.Text.Length > 0)
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.End);

        var result = new List<SubtitleSegment>();
        foreach (SubtitleSegment candidate in candidates)
        {
            if (result.Count > 0 &&
                string.Equals(result[^1].Text, candidate.Text, StringComparison.OrdinalIgnoreCase))
            {
                SubtitleSegment previous = result[^1];
                result[^1] = previous with { End = Max(previous.End, candidate.End) };
                continue;
            }

            if (result.Count > 0 && result[^1].End > candidate.Start)
            {
                SubtitleSegment previous = result[^1];
                result[^1] = previous with { End = Max(previous.Start, candidate.Start) };
            }

            result.Add(candidate);
        }

        return result;
    }

    private static bool IsSpeech(SubtitleSegment segment) =>
        !(segment.NoSpeechProbability >= 0.85 && segment.AverageLogProbability <= -1.0);

    private static SubtitleSegment Normalize(SubtitleSegment segment) =>
        segment with { Text = Whitespace().Replace(segment.Text.Trim(), " ") };

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
