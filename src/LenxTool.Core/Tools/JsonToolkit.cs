using System.Text.Json;
using System.Text.Json.Nodes;

namespace LenxTool.Core.Tools;

public sealed record JsonValidationResult(
    bool IsValid,
    string? Message = null,
    long? LineNumber = null,
    long? BytePositionInLine = null);

public enum JsonDifferenceKind
{
    Added,
    Removed,
    Changed
}

public sealed record JsonDifference(
    string Path,
    JsonDifferenceKind Kind,
    string? LeftValue,
    string? RightValue);

public sealed record JsonDiffResult(
    IReadOnlyList<JsonDifference> Differences,
    bool IsTruncated);

public static class JsonToolkit
{
    private const int MaximumInputCharacters = 10 * 1024 * 1024;

    public static string Format(string input) => Serialize(Parse(input), indented: true);

    public static string Minify(string input) => Serialize(Parse(input), indented: false);

    public static string SortProperties(string input, bool indented = true) =>
        Serialize(SortNode(Parse(input)), indented);

    public static JsonValidationResult Validate(string input)
    {
        try
        {
            Parse(input);
            return new(true);
        }
        catch (JsonException exception)
        {
            return new(false, exception.Message, exception.LineNumber, exception.BytePositionInLine);
        }
        catch (ArgumentException exception)
        {
            return new(false, exception.Message);
        }
    }

    public static IReadOnlyList<JsonDifference> Diff(string left, string right)
    {
        return Diff(
            left,
            right,
            int.MaxValue,
            CancellationToken.None).Differences;
    }

    public static JsonDiffResult Diff(
        string left,
        string right,
        int maximumDifferences,
        CancellationToken cancellationToken = default)
    {
        if (maximumDifferences <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDifferences),
                "差异数量上限必须大于 0。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        JsonNode leftNode = Parse(left);
        cancellationToken.ThrowIfCancellationRequested();
        JsonNode rightNode = Parse(right);
        cancellationToken.ThrowIfCancellationRequested();
        var differences = new JsonDifferenceCollector(
            maximumDifferences,
            cancellationToken);
        Compare(leftNode, rightNode, "$", differences);
        return new(differences.Items, differences.IsTruncated);
    }

    private static JsonNode Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaximumInputCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "JSON 输入不能超过 10 MiB 字符。");
        }

        return JsonNode.Parse(
            input,
            new JsonNodeOptions { PropertyNameCaseInsensitive = false },
            new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow })
            ?? throw new JsonException("JSON 根节点不能为空。");
    }

    private static string Serialize(JsonNode node, bool indented) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    private static JsonNode SortNode(JsonNode node) =>
        node switch
        {
            JsonObject jsonObject => SortObject(jsonObject),
            JsonArray jsonArray => new JsonArray(jsonArray.Select(
                item => item is null ? null : SortNode(item)).ToArray()),
            _ => node.DeepClone()
        };

    private static JsonObject SortObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach ((string key, JsonNode? value) in source.OrderBy(
                     item => item.Key,
                     StringComparer.Ordinal))
        {
            result[key] = value is null ? null : SortNode(value);
        }

        return result;
    }

    private static void Compare(
        JsonNode? left,
        JsonNode? right,
        string path,
        JsonDifferenceCollector differences)
    {
        differences.ThrowIfCancellationRequested();
        if (differences.IsTruncated) return;

        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            foreach (string key in leftObject.Select(item => item.Key)
                         .Union(rightObject.Select(item => item.Key), StringComparer.Ordinal)
                         .OrderBy(key => key, StringComparer.Ordinal))
            {
                differences.ThrowIfCancellationRequested();
                if (differences.IsTruncated) return;

                bool hasLeft = leftObject.TryGetPropertyValue(key, out JsonNode? leftValue);
                bool hasRight = rightObject.TryGetPropertyValue(key, out JsonNode? rightValue);
                string childPath = $"{path}.{key}";
                if (!hasLeft)
                {
                    differences.TryAdd(new(
                        childPath,
                        JsonDifferenceKind.Added,
                        null,
                        Render(rightValue)));
                }
                else if (!hasRight)
                {
                    differences.TryAdd(new(
                        childPath,
                        JsonDifferenceKind.Removed,
                        Render(leftValue),
                        null));
                }
                else
                {
                    Compare(leftValue, rightValue, childPath, differences);
                }
            }

            return;
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            for (int index = 0; index < Math.Max(leftArray.Count, rightArray.Count); index++)
            {
                differences.ThrowIfCancellationRequested();
                if (differences.IsTruncated) return;

                string childPath = $"{path}[{index}]";
                if (index >= leftArray.Count)
                {
                    differences.TryAdd(new(
                        childPath,
                        JsonDifferenceKind.Added,
                        null,
                        Render(rightArray[index])));
                }
                else if (index >= rightArray.Count)
                {
                    differences.TryAdd(new(
                        childPath,
                        JsonDifferenceKind.Removed,
                        Render(leftArray[index]),
                        null));
                }
                else
                {
                    Compare(leftArray[index], rightArray[index], childPath, differences);
                }
            }

            return;
        }

        if (!JsonNode.DeepEquals(left, right))
        {
            differences.TryAdd(new(
                path,
                JsonDifferenceKind.Changed,
                Render(left),
                Render(right)));
        }
    }

    private static string Render(JsonNode? node) => node?.ToJsonString() ?? "null";

    private sealed class JsonDifferenceCollector(
        int maximumDifferences,
        CancellationToken cancellationToken)
    {
        private readonly List<JsonDifference> _items = [];

        public IReadOnlyList<JsonDifference> Items => _items;

        public bool IsTruncated { get; private set; }

        public void ThrowIfCancellationRequested() =>
            cancellationToken.ThrowIfCancellationRequested();

        public void TryAdd(JsonDifference difference)
        {
            ThrowIfCancellationRequested();
            if (_items.Count >= maximumDifferences)
            {
                IsTruncated = true;
                return;
            }

            _items.Add(difference);
        }
    }
}
