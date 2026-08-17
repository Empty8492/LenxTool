using LenxTool.Core.Tools;

namespace LenxTool.Core.Tests.Tools;

public sealed class ToolboxTests
{
    [Fact]
    public void JsonToolkitFormatsSortsAndReportsStructuralDifferences()
    {
        const string input = "{\"z\":2,\"a\":{\"d\":4,\"b\":1}}";

        string sorted = JsonToolkit.SortProperties(input, indented: false);
        IReadOnlyList<JsonDifference> differences = JsonToolkit.Diff(
            sorted,
            "{\"a\":{\"b\":2,\"d\":4},\"z\":2,\"new\":true}");

        Assert.Equal("{\"a\":{\"b\":1,\"d\":4},\"z\":2}", sorted);
        Assert.Contains(differences, item => item.Path == "$[\"a\"][\"b\"]" && item.Kind == JsonDifferenceKind.Changed);
        Assert.Contains(differences, item => item.Path == "$[\"new\"]" && item.Kind == JsonDifferenceKind.Added);
    }

    [Fact]
    public void JsonToolkitDiffSupportsCancellationAndBoundedResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            JsonToolkit.Diff("{}", "{}", 10, cancellation.Token));

        JsonDiffResult result = JsonToolkit.Diff(
            "{\"a\":1,\"b\":2,\"c\":3}",
            "{\"a\":4,\"b\":5,\"c\":6}",
            maximumDifferences: 2);

        Assert.Equal(2, result.Differences.Count);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public void JsonToolkitDiffEscapesEveryObjectKeyWithoutPathCollisions()
    {
        const string left =
            "{\"a.b\":1,\"a\":{\"b\":1},\"quote\\\"key\":1,\"\":1}";
        const string right =
            "{\"a.b\":2,\"a\":{\"b\":2},\"quote\\\"key\":2,\"\":2}";

        JsonDiffResult result = JsonToolkit.Diff(
            left,
            right,
            maximumDifferences: 10);

        Assert.Equal(
            ["$[\"\"]", "$[\"a\"][\"b\"]", "$[\"a.b\"]", "$[\"quote\\u0022key\"]"],
            result.Differences.Select(item => item.Path));
        Assert.Equal(
            result.Differences.Count,
            result.Differences.Select(item => item.Path)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void JsonToolkitAcceptsNullAsAValidRootValue()
    {
        Assert.True(JsonToolkit.Validate("null").IsValid);
        Assert.Equal("null", JsonToolkit.Format("null"));
        Assert.Equal("null", JsonToolkit.Minify("null"));
        Assert.Equal("null", JsonToolkit.SortProperties("null"));

        JsonDiffResult equal = JsonToolkit.Diff(
            "null",
            "null",
            maximumDifferences: 10);
        JsonDiffResult changed = JsonToolkit.Diff(
            "null",
            "false",
            maximumDifferences: 10);

        Assert.Empty(equal.Differences);
        JsonDifference difference = Assert.Single(changed.Differences);
        Assert.Equal("$", difference.Path);
        Assert.Equal(JsonDifferenceKind.Changed, difference.Kind);
        Assert.Equal("null", difference.LeftValue);
        Assert.Equal("false", difference.RightValue);
    }

    [Fact]
    public async Task JsonToolkitAnalysisKeepsValidNullDistinctFromParseFailure()
    {
        JsonDiffAnalysisResult result = await JsonToolkit.AnalyzeDiffAsync(
            "null",
            "{\"value\":1}",
            maximumDifferences: 10,
            maximumInputCharacters: 1_024,
            CancellationToken.None);

        Assert.True(result.LeftValidation.IsValid);
        Assert.True(result.RightValidation.IsValid);
        JsonDifference difference = Assert.Single(result.Diff!.Differences);
        Assert.Equal("$", difference.Path);
        Assert.Equal("null", difference.LeftValue);
        Assert.Equal("{\"value\":1}", difference.RightValue);
    }

    [Fact]
    public void JsonToolkitDiffBoundsLongPathsWithoutCollisions()
    {
        string longKey = new('k', 8_000);
        string left = "{\"" + longKey + "\":{" + string.Join(
            ',',
            Enumerable.Range(0, 100)
                .Select(index => $"\"p{index:D3}\":0")) + "}}";
        string right = "{\"" + longKey + "\":{" + string.Join(
            ',',
            Enumerable.Range(0, 100)
                .Select(index => $"\"p{index:D3}\":1")) + "}}";

        JsonDiffResult result = JsonToolkit.Diff(
            left,
            right,
            maximumDifferences: 500);

        Assert.Equal(100, result.Differences.Count);
        Assert.All(
            result.Differences,
            difference => Assert.InRange(
                difference.Path.Length,
                1,
                1_024));
        Assert.Equal(
            result.Differences.Count,
            result.Differences.Select(item => item.Path)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(
            result.Differences,
            difference => Assert.Contains(
                "path-sha256:",
                difference.Path,
                StringComparison.Ordinal));
    }

    [Fact]
    public void JsonToolkitDiffDoesNotCopyLongAncestorForEveryDifference()
    {
        string longKey = new('k', 256 * 1_024);
        string left = "{\"" + longKey + "\":{" + string.Join(
            ',',
            Enumerable.Range(0, 500)
                .Select(index => $"\"p{index:D3}\":0")) + "}}";
        string right = "{\"" + longKey + "\":{" + string.Join(
            ',',
            Enumerable.Range(0, 500)
                .Select(index => $"\"p{index:D3}\":1")) + "}}";

        _ = JsonToolkit.Diff(
            "{\"" + new string('w', 2_000) + "\":0}",
            "{\"" + new string('w', 2_000) + "\":1}",
            maximumDifferences: 1);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        JsonDiffResult result = JsonToolkit.Diff(
            left,
            right,
            maximumDifferences: 500);

        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(500, result.Differences.Count);
        Assert.InRange(
            allocatedBytes,
            1,
            64L * 1_024 * 1_024);
    }

    [Fact]
    public void JsonToolkitDiffEnforcesAggregatePathCharacterBudget()
    {
        string prefix = new('p', 700);
        string left = "{" + string.Join(
            ',',
            Enumerable.Range(0, 500)
                .Select(index => $"\"{prefix}{index:D3}\":0")) + "}";
        string right = "{" + string.Join(
            ',',
            Enumerable.Range(0, 500)
                .Select(index => $"\"{prefix}{index:D3}\":1")) + "}";

        JsonDiffResult result = JsonToolkit.Diff(
            left,
            right,
            maximumDifferences: 500);

        Assert.True(result.IsTruncated);
        Assert.True(result.Differences.Count < 500);
        Assert.InRange(
            result.Differences.Sum(item => item.Path.Length),
            1,
            256 * 1_024);
        Assert.Throws<InvalidOperationException>(() =>
            JsonToolkit.Diff(left, right));
    }

    [Fact]
    public async Task JsonToolkitAnalysisReportsBothSidesWithOneParsedTreeEach()
    {
        JsonDiffAnalysisResult result = await JsonToolkit.AnalyzeDiffAsync(
            "{\"broken\":}",
            "{\"valid\":true}",
            maximumDifferences: 10,
            maximumInputCharacters: 1_024,
            CancellationToken.None);

        Assert.False(result.LeftValidation.IsValid);
        Assert.True(result.RightValidation.IsValid);
        Assert.Null(result.Diff);
    }

    [Fact]
    public async Task JsonToolkitAnalysisCanCancelWhileReadingLargeJson()
    {
        const int maximumCharacters = 10 * 1024 * 1024;
        string largeJson = "[0" + new string(
            ' ',
            maximumCharacters - 3) + "]";
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            JsonToolkit.AnalyzeDiffAsync(
                largeJson,
                largeJson,
                maximumDifferences: 10,
                maximumInputCharacters: maximumCharacters,
                cancellation.Token));
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(501, true)]
    public void JsonToolkitDiffMarksOnlyResultsBeyondTheLimitAsTruncated(
        int propertyCount,
        bool expectedTruncated)
    {
        string left = "{" + string.Join(
            ',',
            Enumerable.Range(0, propertyCount)
                .Select(index => $"\"p{index:D3}\":0")) + "}";
        string right = "{" + string.Join(
            ',',
            Enumerable.Range(0, propertyCount)
                .Select(index => $"\"p{index:D3}\":1")) + "}";

        JsonDiffResult result = JsonToolkit.Diff(
            left,
            right,
            maximumDifferences: 500);

        Assert.Equal(500, result.Differences.Count);
        Assert.Equal(expectedTruncated, result.IsTruncated);
    }

    [Fact]
    public void EncodingToolkitRoundTripsChineseAndUrlContent()
    {
        const string text = "Lenx Tools / 本地优先";

        string base64 = EncodingToolkit.ToBase64(text);
        string encodedUrl = EncodingToolkit.EncodeUrl(text);

        Assert.Equal(text, EncodingToolkit.FromBase64(base64));
        Assert.Equal(text, EncodingToolkit.DecodeUrl(encodedUrl));
    }

    [Fact]
    public void TextToolkitRemovesDuplicateAndExcessBlankLinesWithoutReordering()
    {
        const string input = "第一行\r\n\r\n\r\n第二行\r\n第一行\r\n第三行";

        string result = TextToolkit.Clean(input, removeDuplicateLines: true, collapseBlankLines: true);

        Assert.Equal("第一行\n\n第二行\n第三行", result);
    }

    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("{\"a\":}", false)]
    public void JsonValidationReturnsActionableLocation(string input, bool expectedValid)
    {
        JsonValidationResult result = JsonToolkit.Validate(input);

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.NotNull(result.LineNumber);
            Assert.NotNull(result.BytePositionInLine);
        }
    }
}
