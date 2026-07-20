namespace LenxTool.Core.Media;

public sealed record AudioChunk(int Index, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

public static class AudioChunkPlanner
{
    public static IReadOnlyList<AudioChunk> Plan(
        TimeSpan totalDuration,
        TimeSpan chunkDuration,
        TimeSpan overlap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(chunkDuration, TimeSpan.Zero);
        if (overlap < TimeSpan.Zero || overlap >= chunkDuration) throw new ArgumentOutOfRangeException(nameof(overlap));

        TimeSpan step = chunkDuration - overlap;
        var chunks = new List<AudioChunk>();
        for (TimeSpan start = TimeSpan.Zero; start < totalDuration; start += step)
        {
            TimeSpan end = start + chunkDuration < totalDuration ? start + chunkDuration : totalDuration;
            chunks.Add(new(chunks.Count, start, end));
            if (end == totalDuration) break;
        }

        return chunks;
    }
}
