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
        string[] lines = normalized.Split('\n');
        var segments = new List<SubtitleSegment>();

        int index = 0;
        while (index < lines.Length)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
            if (index >= lines.Length) break;

            int sequenceLineNumber = index + 1;
            string sequenceText = lines[index].Trim();
            if (segments.Count == 0) sequenceText = sequenceText.TrimStart('\uFEFF');
            if (!int.TryParse(
                    sequenceText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int sequence))
            {
                throw new FormatException($"第 {sequenceLineNumber} 行应为非负整数字幕序号。");
            }
            index++;

            if (index >= lines.Length)
            {
                throw new FormatException($"第 {sequenceLineNumber} 行字幕缺少时间轴。");
            }
            int timelineLineNumber = index + 1;
            Match timeline = TimelinePattern().Match(lines[index]);
            if (!timeline.Success)
            {
                throw new FormatException(
                    $"第 {timelineLineNumber} 行不是有效时间轴，应使用 00:00:00,000 --> 00:00:00,000。");
            }
            TimeSpan start = ParseTime(timeline.Groups["start"].Value, timelineLineNumber);
            TimeSpan end = ParseTime(timeline.Groups["end"].Value, timelineLineNumber);
            if (end <= start)
            {
                throw new FormatException($"第 {timelineLineNumber} 行的结束时间必须晚于开始时间。");
            }
            index++;

            int textLineNumber = index + 1;
            var textLines = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                textLines.Add(lines[index]);
                index++;
            }
            string text = string.Join('\n', textLines).Trim();
            if (text.Length == 0)
            {
                throw new FormatException($"第 {textLineNumber} 行应包含字幕正文。");
            }

            segments.Add(new(start, end, text) { Sequence = sequence });
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
            builder.Append(segment.Sequence ?? checked(index + 1)).Append('\n');
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

    private static TimeSpan ParseTime(string value, int lineNumber)
    {
        Match match = TimePattern().Match(value);
        if (!match.Success) throw new FormatException($"第 {lineNumber} 行包含无效的 SRT 时间：{value}");

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

    [GeneratedRegex("^\\s*(?<start>\\d{2,}:\\d{2}:\\d{2},\\d{3})\\s*-->\\s*(?<end>\\d{2,}:\\d{2}:\\d{2},\\d{3})(?:\\s+.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimelinePattern();

    [GeneratedRegex("^(?<h>\\d{2,}):(?<m>[0-5]\\d):(?<s>[0-5]\\d),(?<ms>\\d{3})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();
}
