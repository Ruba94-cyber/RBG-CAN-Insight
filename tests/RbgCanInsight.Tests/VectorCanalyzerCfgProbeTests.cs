using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class VectorCanalyzerCfgProbeTests
{
    [Theory]
    [InlineData("measurement.CFG")]
    [InlineData("measurement.cfg")]
    public void CfgExtensionIsReportedAsUnsupportedCandidateWithoutClaimingFormat(string path)
    {
        Assert.True(VectorCanalyzerCfgProbe.IsUnsupportedCandidate(path));

        var message = VectorCanalyzerCfgProbe.DescribeUnsupported(path);
        Assert.Contains("may be a CANalyzer configuration", message, StringComparison.Ordinal);
        Assert.Contains("not imported", message, StringComparison.Ordinal);
        Assert.Contains("public format specification is unavailable", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("network.dbc")]
    [InlineData("capture.blf")]
    public void OtherExtensionsAreNotClassifiedAsCanalyzerCandidates(string path)
    {
        Assert.False(VectorCanalyzerCfgProbe.IsUnsupportedCandidate(path));
        Assert.Throws<ArgumentException>(() => VectorCanalyzerCfgProbe.DescribeUnsupported(path));
    }
}
