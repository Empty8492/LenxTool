using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LenxTool.Core.Models;

namespace LenxTool.Core.Media;

public enum SubtitleExportMode
{
    OriginalSrt,
    BilingualSrt,
    PlainText
}

public static partial class SrtCodec
{
    public static IReadOnlyList<SubtitleSegment> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var segments = new List<SubtitleSegment>();

        foreach (Match match in BlockPattern().Matches(normalized))
        {
            TimeSpan start = ParseTime(match.Groups["start"].Value);
            TimeSpan end = ParseTime(match.Groups["end"].Value);
            string text = match.Groups["text"].Value.TrimEnd('\n').Trim();
            if (end > start && text.Length > 0)
            {
                segments.Add(new(start, end, text));
            }
        }

        return segments;
    }

    public static string Export(
        IReadOnlyList<SubtitleSegment> segments,
        SubtitleExportMode mode)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (mode == SubtitleExportMode.PlainText)
        {
            return string.Join(
                '\n',
                segments.Select(segment =>
                    string.IsNullOrWhiteSpace(segment.TranslatedText)
                        ? segment.Text
                        : segment.TranslatedText));
        }

        var builder = new StringBuilder();
        for (int index = 0; index < segments.Count; index++)
        {
            SubtitleSegment segment = segments[index];
            builder.Append(index + 1).Append('\n');
            builder.Append(FormatTime(segment.Start)).Append(" --> ")
                .Append(FormatTime(segment.End)).Append('\n');

            if (mode == SubtitleExportMode.BilingualSrt &&
                !string.IsNullOrWhiteSpace(segment.TranslatedText))
            {
                builder.Append(segment.TranslatedText!.Trim()).Append('\n');
            }

            builder.Append(segment.Text.Trim()).Append("\n\n");
        }

        return builder.ToString();
    }

    private static TimeSpan ParseTime(string value)
    {
        Match match = TimePattern().Match(value);
        if (!match.Success) throw new FormatException($"无效的 SRT 时间：{value}");

        long hours = long.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        int milliseconds = int.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture);
        return TimeSpan.FromHours(hours) + new TimeSpan(0, 0, minutes, seconds, milliseconds);
    }

    private static string FormatTime(TimeSpan value)
    {
        long hours = (long)Math.Floor(value.TotalHours);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}");
    }

    [GeneratedRegex("(?ms)^\\s*\\d+\\s*\\n(?<start>\\d{2,}:\\d{2}:\\d{2},\\d{3})\\s*-->\\s*(?<end>\\d{2,}:\\d{2}:\\d{2},\\d{3})[^\\n]*\\n(?<text>.*?)(?=\\n{2,}|\\z)")]
    private static partial Regex BlockPattern();

    [GeneratedRegex("^(?<h>\\d{2,}):(?<m>[0-5]\\d):(?<s>[0-5]\\d),(?<ms>\\d{3})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();
}
