using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>J1939 signal searches must operate on actually observed
/// 29-bit CAN headers, not DBC PGN aliases or an inferred ECU name.</summary>
public sealed class RecordedCanSourceQueryTests
{
    private const uint Eec1SourceFe = 0x0CF004FE;
    private const uint Eec1Source00 = 0x0CF00400;
    private const uint Tsc1SourceFe = 0x0C0003FE;

    [Theory]
    [InlineData("sa:FE")]
    [InlineData("SA:0xFE")]
    [InlineData("sa:0Xfe")]
    [InlineData("sa:000000FE")]
    public void SourceAddressQueryMatchesExactRecordedEcuAddress(string term)
    {
        Assert.True(RecordedCanSourceQuery.IsSourceFilter(term));
        Assert.True(RecordedCanSourceQuery.Matches(Eec1SourceFe, true, term));
        Assert.True(RecordedCanSourceQuery.Matches(Tsc1SourceFe, true, term));
        Assert.False(RecordedCanSourceQuery.Matches(Eec1Source00, true, term));
        Assert.False(RecordedCanSourceQuery.Matches(Eec1SourceFe, false, term));
    }

    [Theory]
    [InlineData("pgn:F004")]
    [InlineData("PGN:0xF004")]
    [InlineData("pgn:0Xf004")]
    public void PgnQueryMatchesSameGroupRegardlessOfSourceAddress(string term)
    {
        Assert.True(RecordedCanSourceQuery.Matches(Eec1SourceFe, true, term));
        Assert.True(RecordedCanSourceQuery.Matches(Eec1Source00, true, term));
        Assert.False(RecordedCanSourceQuery.Matches(Tsc1SourceFe, true, term));
        Assert.False(RecordedCanSourceQuery.Matches(Eec1SourceFe, false, term));
    }

    [Theory]
    [InlineData("sa:")]
    [InlineData("sa:0x")]
    [InlineData("sa:100")]
    [InlineData("sa:zz")]
    [InlineData("sa:+FE")]
    [InlineData("pgn:")]
    [InlineData("pgn:0x40000")]
    [InlineData("pgn:F004garbage")]
    public void MalformedOrOutOfRangeFiltersNeverMatchArbitrarySources(string term)
    {
        Assert.True(RecordedCanSourceQuery.IsSourceFilter(term));
        Assert.False(RecordedCanSourceQuery.Matches(Eec1SourceFe, true, term));
        Assert.False(RecordedCanSourceQuery.Matches(Eec1Source00, true, term));
    }

    [Fact]
    public void SourceFilterCannotBecomeGeneralTextSearchOrMatchUnverifiedStandardFrame()
    {
        Assert.False(RecordedCanSourceQuery.IsSourceFilter("0x0CF004FE"));
        Assert.False(RecordedCanSourceQuery.IsSourceFilter("EngineSpeed"));
        Assert.False(RecordedCanSourceQuery.IsSourceFilter("dbc:truck"));
        Assert.False(RecordedCanSourceQuery.Matches(Eec1SourceFe, true, "EngineSpeed"));
        Assert.False(RecordedCanSourceQuery.Matches(Eec1SourceFe, false, "sa:FE"));
    }
}
