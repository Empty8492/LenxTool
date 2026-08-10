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
        Assert.Contains(differences, item => item.Path == "$.a.b" && item.Kind == JsonDifferenceKind.Changed);
        Assert.Contains(differences, item => item.Path == "$.new" && item.Kind == JsonDifferenceKind.Added);
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
