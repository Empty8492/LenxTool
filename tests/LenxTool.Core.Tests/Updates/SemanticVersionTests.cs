using LenxTool.Core.Updates;

namespace LenxTool.Core.Tests.Updates;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0-alpha.1", "2.0.0", -1)]
    [InlineData("2.0.0+build.7", "2.0.0+build.2", 0)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.2", 1)]
    public void CompareFollowsSemanticVersionPrecedence(string left, string right, int expectedSign)
    {
        SemanticVersion leftVersion = SemanticVersion.Parse(left);
        SemanticVersion rightVersion = SemanticVersion.Parse(right);

        Assert.Equal(expectedSign, Math.Sign(leftVersion.CompareTo(rightVersion)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.-1")]
    [InlineData("v1.2.3")]
    public void TryParseRejectsInvalidVersions(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
    }
}
