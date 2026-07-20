namespace LenxTool.Core.Tools;

public static class TextToolkit
{
    public static string Clean(
        string input,
        bool removeDuplicateLines,
        bool collapseBlankLines)
    {
        ArgumentNullException.ThrowIfNull(input);
        string[] lines = input.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var output = new List<string>(lines.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool previousBlank = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            bool blank = string.IsNullOrWhiteSpace(line);
            if (blank)
            {
                if (!collapseBlankLines || !previousBlank) output.Add(string.Empty);
                previousBlank = true;
                continue;
            }

            previousBlank = false;
            if (!removeDuplicateLines || seen.Add(line)) output.Add(line);
        }

        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
        return string.Join('\n', output);
    }
}
