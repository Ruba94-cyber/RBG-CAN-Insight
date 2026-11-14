using RbgCanInsight.Analysis.Heuristics;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceDbcCoverageAnalyzerTests
{
    private const string Dbc = """
        VERSION ""
        NS_ :
        BS_:
        BU_: A B
        BO_ 256 Engine: 8 A
         SG_ Speed : 0|16@1+ (1,0) [0|200] "km/h" B
         SG_ Temp : 16|8@1+ (1,-40) [-40|120] "degC" B
        """;

    private static CanFrame F(uint id, byte[] data, long ts = 0, int channel = 0) =>
        new(channel, id, data, ts, CanFrameFlags.None);

    private static DbcDatabase Db() => new DbcParser().Parse("t", new StringReader(Dbc));

    [Fact]
    public void ReportsUnmatchedIdsUncoveredBytesShortFramesAndOutOfRangeValues()
    {
        var frames = new[]
        {
            F(0x100, [0x10, 0x00, 0x50, 0, 0, 0, 0, 0]),   // speed 16, temp 40: in range
            F(0x100, [0xFF, 0x00, 0x50, 0, 0, 0, 0, 0]),   // speed 255 > 200: out of range
            F(0x100, [0x10, 0x00, 0xC8, 0, 0, 0, 0, 0]),   // temp 160 > 120: out of range
            F(0x100, [0x10, 0x00]),                          // short: temp not decodable
            F(0x7DF, [0x02, 0x01, 0x0C]),                    // unknown id
            F(0x7DF, [0x02, 0x01, 0x0D]),
            F(0x555, [1, 2, 3, 4], channel: 1)               // unknown id on another channel
        };

        var report = TraceDbcCoverageAnalyzer.Analyze(frames, Db());

        Assert.Equal(7, report.FrameCount);
        Assert.Equal(4, report.MatchedFrames);
        Assert.Equal(3, report.UnmatchedFrames);
        Assert.Equal(2, report.UnmatchedIds.Count);
        var first = report.UnmatchedIds[0];
        Assert.Equal((0x7DFu, 2L, 6L, 4), (first.Identifier, first.FrameCount, first.PayloadBytes, first.FirstFrameIndex));
        Assert.Equal(1, report.UnmatchedIds[1].Channel);

        var message = Assert.Single(report.Messages);
        Assert.Equal(4, message.Frames);
        Assert.Equal(1, message.ShortFrames);
        Assert.Equal(3, message.FirstShortFrameIndex);
        Assert.Equal([3, 4, 5, 6, 7], message.UncoveredBytes);
        Assert.Equal(4 * 5, message.UndecodableBytes);
        Assert.Equal(6 + 4 + 20, report.UndecodableBytes);

        Assert.Equal(2, report.OutOfRange.Count);
        var speed = report.OutOfRange.Single(o => o.Signal == "Speed");
        Assert.Equal(1, speed.OutOfRangeSamples);
        Assert.Equal(255d, speed.ObservedMax);
        Assert.Equal(1, speed.FirstFrameIndex);
        Assert.Equal(1, report.OutOfRange.Single(o => o.Signal == "Temp").OutOfRangeSamples);
    }

    [Fact]
    public void ErrorAndRemoteFramesAreIgnoredAndCleanTraceHasNoFindings()
    {
        var frames = new[]
        {
            F(0x100, [0x10, 0x00, 0x50, 0, 0, 0, 0, 0]),
            new CanFrame(0, 0x100, [0, 0], 1, CanFrameFlags.Error),
            new CanFrame(0, 0x321, [], 2, CanFrameFlags.Remote)
        };
        var report = TraceDbcCoverageAnalyzer.Analyze(frames, Db());
        Assert.Equal(1, report.FrameCount);
        Assert.Empty(report.UnmatchedIds);
        Assert.Empty(report.OutOfRange);
        Assert.Equal(0, report.Messages.Single().ShortFrames);
    }

    [Fact]
    public void SignalsWithoutDeclaredRangeAreNeverReportedOutOfRange()
    {
        var dbc = new DbcParser().Parse("n", new StringReader(
            "VERSION \"\"\nNS_ :\nBS_:\nBU_: A\nBO_ 1 M: 8 A\n SG_ S : 0|8@1+ (1,0) [0|0] \"\" Vector__XXX\n"));
        var report = TraceDbcCoverageAnalyzer.Analyze([F(1, [0xFF, 0, 0, 0, 0, 0, 0, 0])], dbc);
        Assert.Empty(report.OutOfRange);
    }
}
