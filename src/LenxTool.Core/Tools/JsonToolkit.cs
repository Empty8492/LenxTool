using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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

public sealed record JsonDiffAnalysisResult(
    JsonValidationResult LeftValidation,
    JsonValidationResult RightValidation,
    JsonDiffResult? Diff);

public static class JsonToolkit
{
    private const int MaximumInputCharacters = 10 * 1024 * 1024;
    private const int MaximumRenderedPathCharacters = 1_024;
    private const int MaximumTotalRenderedPathCharacters = 256 * 1_024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

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
        JsonDiffResult result = Diff(
            left,
            right,
            int.MaxValue,
            CancellationToken.None);
        if (result.IsTruncated)
        {
            throw new InvalidOperationException(
                "JSON 差异路径超过安全输出预算；请使用可返回截断状态的有界 Diff 重载。");
        }
        return result.Differences;
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
        JsonNode? leftNode = Parse(left);
        cancellationToken.ThrowIfCancellationRequested();
        JsonNode? rightNode = Parse(right);
        cancellationToken.ThrowIfCancellationRequested();
        var differences = new JsonDifferenceCollector(
            maximumDifferences,
            cancellationToken);
        Compare(leftNode, rightNode, BoundedJsonPath.Root, differences);
        return new(differences.Items, differences.IsTruncated);
    }

    public static async Task<JsonDiffAnalysisResult> AnalyzeDiffAsync(
        string left,
        string right,
        int maximumDifferences,
        int maximumInputCharacters,
        CancellationToken cancellationToken = default)
    {
        if (maximumDifferences <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDifferences),
                "差异数量上限必须大于 0。");
        }
        if (maximumInputCharacters <= 0
            || maximumInputCharacters > MaximumInputCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInputCharacters),
                $"JSON 输入字符上限必须介于 1 和 {MaximumInputCharacters:N0} 之间。");
        }

        ParsedJson leftParsed = await TryParseAsync(
                left,
                maximumInputCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        ParsedJson rightParsed = await TryParseAsync(
                right,
                maximumInputCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        if (!leftParsed.Validation.IsValid
            || !rightParsed.Validation.IsValid)
        {
            return new(
                leftParsed.Validation,
                rightParsed.Validation,
                null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var differences = new JsonDifferenceCollector(
            maximumDifferences,
            cancellationToken);
        Compare(
            leftParsed.Node,
            rightParsed.Node,
            BoundedJsonPath.Root,
            differences);
        return new(
            leftParsed.Validation,
            rightParsed.Validation,
            new(differences.Items, differences.IsTruncated));
    }

    private static JsonNode? Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaximumInputCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "JSON 输入不能超过 10 MiB 字符。");
        }

        return JsonNode.Parse(
            input,
            new JsonNodeOptions { PropertyNameCaseInsensitive = false },
            new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
    }

    private static string Serialize(JsonNode? node, bool indented) =>
        node?.ToJsonString(new JsonSerializerOptions { WriteIndented = indented })
        ?? "null";

    private static JsonNode? SortNode(JsonNode? node) =>
        node switch
        {
            null => null,
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
        BoundedJsonPath path,
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
                BoundedJsonPath childPath = path.AppendProperty(key);
                if (!hasLeft)
                {
                    differences.TryAdd(new(
                        childPath.Display,
                        JsonDifferenceKind.Added,
                        null,
                        Render(rightValue)));
                }
                else if (!hasRight)
                {
                    differences.TryAdd(new(
                        childPath.Display,
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

                BoundedJsonPath childPath = path.AppendIndex(index);
                if (index >= leftArray.Count)
                {
                    differences.TryAdd(new(
                        childPath.Display,
                        JsonDifferenceKind.Added,
                        null,
                        Render(rightArray[index])));
                }
                else if (index >= rightArray.Count)
                {
                    differences.TryAdd(new(
                        childPath.Display,
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
                path.Display,
                JsonDifferenceKind.Changed,
                Render(left),
                Render(right)));
        }
    }

    private static string Render(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static async Task<ParsedJson> TryParseAsync(
        string input,
        int maximumInputCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > maximumInputCharacters)
        {
            return new(
                new(
                    false,
                    $"超过 {maximumInputCharacters:N0} 字符上限"),
                null);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] utf8 = StrictUtf8.GetBytes(input);
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new ChunkedReadOnlyStream(utf8);
            JsonNode? node = await JsonNode.ParseAsync(
                    stream,
                    new JsonNodeOptions
                    {
                        PropertyNameCaseInsensitive = false
                    },
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(new(true), node);
        }
        catch (JsonException exception)
        {
            return new(
                new(
                    false,
                    exception.Message,
                    exception.LineNumber,
                    exception.BytePositionInLine),
                null);
        }
        catch (ArgumentException exception)
        {
            return new(new(false, exception.Message), null);
        }
    }

    private sealed record ParsedJson(
        JsonValidationResult Validation,
        JsonNode? Node);

    private sealed class ChunkedReadOnlyStream(byte[] content) : Stream
    {
        private const int MaximumChunkBytes = 64 * 1024;

        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadCore(buffer.AsSpan(offset, count));
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return ReadCore(buffer.Span);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken)
                .AsTask();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        private int ReadCore(Span<byte> destination)
        {
            int count = Math.Min(
                Math.Min(destination.Length, MaximumChunkBytes),
                content.Length - _position);
            if (count <= 0) return 0;

            content.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }
    }

    /// <summary>
    /// 在遍历时共享祖先状态；只有不超过预算的路径才构造完整字符串。
    /// 超长路径使用基于完整段链的 SHA-256 标识，避免重复复制巨型祖先键。
    /// </summary>
    private sealed class BoundedJsonPath
    {
        private readonly string? _fullPath;
        private readonly BoundedJsonPath? _parent;
        private readonly string? _propertyKey;
        private readonly int _arrayIndex;
        private readonly byte _segmentKind;
        private byte[]? _digest;
        private string? _display;

        private BoundedJsonPath(
            string fullPath)
        {
            _fullPath = fullPath;
        }

        private BoundedJsonPath(
            BoundedJsonPath parent,
            string propertyKey)
        {
            _parent = parent;
            _propertyKey = propertyKey;
            _segmentKind = 1;
        }

        private BoundedJsonPath(
            BoundedJsonPath parent,
            int arrayIndex)
        {
            _parent = parent;
            _arrayIndex = arrayIndex;
            _segmentKind = 2;
        }

        public static BoundedJsonPath Root { get; } = new("$");

        public string Display => _fullPath
            ?? (_display ??=
                $"$[<path-sha256:{Convert.ToHexString(GetDigest())}>]");

        public BoundedJsonPath AppendProperty(string key)
        {
            if (_fullPath is not null
                && key.Length
                <= MaximumRenderedPathCharacters
                    - _fullPath.Length
                    - 4)
            {
                string serializedKey = JsonSerializer.Serialize(key);
                if (_fullPath.Length + serializedKey.Length + 2
                    <= MaximumRenderedPathCharacters)
                {
                    return new(string.Concat(
                            _fullPath,
                            "[",
                            serializedKey,
                            "]"));
                }
            }

            return new(this, key);
        }

        public BoundedJsonPath AppendIndex(int index)
        {
            if (_fullPath is not null)
            {
                string suffix = $"[{index}]";
                if (_fullPath.Length + suffix.Length
                    <= MaximumRenderedPathCharacters)
                {
                    return new(string.Concat(_fullPath, suffix));
                }
            }

            return new(this, index);
        }

        private byte[] GetDigest()
        {
            if (_digest is not null)
            {
                return _digest;
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            AppendParentIdentity(hash);
            if (_segmentKind == 1)
            {
                AppendPropertySegment(hash, _propertyKey!);
            }
            else
            {
                AppendIndexSegment(hash, _arrayIndex);
            }
            _digest = hash.GetHashAndReset();
            return _digest;
        }

        private void AppendParentIdentity(IncrementalHash hash)
        {
            if (_parent!._fullPath is { } fullParent)
            {
                Span<byte> header = stackalloc byte[5];
                header[0] = 0;
                BinaryPrimitives.WriteInt32LittleEndian(
                    header[1..],
                    fullParent.Length);
                hash.AppendData(header);
                hash.AppendData(MemoryMarshal.AsBytes(
                    fullParent.AsSpan()));
                return;
            }

            hash.AppendData([3]);
            hash.AppendData(_parent.GetDigest());
        }

        private static void AppendPropertySegment(
            IncrementalHash hash,
            string key)
        {
            Span<byte> header = stackalloc byte[5];
            header[0] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(
                header[1..],
                key.Length);
            hash.AppendData(header);
            hash.AppendData(MemoryMarshal.AsBytes(key.AsSpan()));
        }

        private static void AppendIndexSegment(
            IncrementalHash hash,
            int index)
        {
            Span<byte> segment = stackalloc byte[5];
            segment[0] = 2;
            BinaryPrimitives.WriteInt32LittleEndian(
                segment[1..],
                index);
            hash.AppendData(segment);
        }
    }

    private sealed class JsonDifferenceCollector(
        int maximumDifferences,
        CancellationToken cancellationToken)
    {
        private readonly List<JsonDifference> _items = [];
        private int _totalPathCharacters;

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
            if (_totalPathCharacters + difference.Path.Length
                > MaximumTotalRenderedPathCharacters)
            {
                IsTruncated = true;
                return;
            }

            _items.Add(difference);
            _totalPathCharacters += difference.Path.Length;
        }
    }
}
