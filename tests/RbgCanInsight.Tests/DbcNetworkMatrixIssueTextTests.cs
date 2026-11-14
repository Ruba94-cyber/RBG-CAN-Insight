using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcNetworkMatrixIssueTextTests
{
    [Fact]
    public void ChannelIsShownOneBasedLikeTheRestOfTheApp()
    {
        var issue = new DbcNetworkMatrixIssue(0, 0x100, false, 5, "NO_ACTIVE_DBC", "x");
        Assert.Equal("CAN 1", issue.ChannelText);
    }

    [Theory]
    [InlineData("NO_ACTIVE_DBC", "No active DBC describes this ID")]
    [InlineData("AMBIGUOUS_ACTIVE_DBC", "Several DBCs claim this ID")]
    [InlineData("OBSERVED_DLC_EXCEEDS_DEFINITION", "Payload longer than the DBC length")]
    [InlineData("SOME_NEW_CODE", "some new code")]
    public void RawCodesBecomeSentencesButStayAvailable(string code, string expected)
    {
        var issue = new DbcNetworkMatrixIssue(1, 0x100, false, 5, code, "x");
        Assert.Equal(expected, issue.CodeText);
        Assert.Equal(code, issue.Code);
    }
}
