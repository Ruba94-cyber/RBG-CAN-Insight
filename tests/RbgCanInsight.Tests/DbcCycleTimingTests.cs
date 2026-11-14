using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcCycleTimingTests
{
    private static CanFrame Frame(int ch, uint id, int ms,
        CanFrameFlags flags = CanFrameFlags.None) =>
        new(ch, id,
            flags.HasFlag(CanFrameFlags.Remote)
                ? ReadOnlySpan<byte>.Empty
                : [0x01, 0x10, 0, 0, 0, 0, 0, 0],
            (long)ms * 1_000_000, flags);

    private static DbcDatabase Database(int cycleMs = 100) =>
        new DbcParser().Parse("test", new StringReader(
            $"""
             BO_ 256 EngineData: 8 ECM
              SG_ Signal : 0|8@1+ (1,0) [0|255] "" ECM
             BA_ "GenMsgCycleTime" BO_ 256 {cycleMs};
             """));

    private static DbcCycleTimingResult Analyze(IReadOnlyList<CanFrame> frames,
        IReadOnlyList<DbcDecoder> decoders, int maxIncidents = 100) =>
        DbcCycleTimingAnalyzer.Analyze(frames,
            TraceStatistics.Compute(frames), decoders, maxIncidents: maxIncidents);

    [Fact]
    public void ComputesNominalDeviationAndLinksLateGapsToActualFrameIndex()
    {
        var frames = new[]
        {
            Frame(0, 0x100, 0), Frame(0, 0x100, 100), Frame(0, 0x100, 200),
            Frame(0, 0x100, 500)
        };
        var result = Analyze(frames, [new DbcDecoder(Database())]);
        var row = Assert.Single(result.Rows);
        Assert.Equal(100, row.NominalCycleMs);
        Assert.Equal(4, row.RecordedFrameCount);
        Assert.Equal(166.6666667, row.AveragePeriodMs, precision: 5);
        Assert.Equal(100, row.MinPeriodMs);
        Assert.Equal(300, row.MaxPeriodMs);
        Assert.Equal(1, row.LateGapCount);
        Assert.Equal(0, row.EarlyGapCount);
        Assert.Equal(2, row.PotentialMissingFrames);
        var incident = Assert.Single(result.Incidents);
        Assert.Equal(3, incident.FrameIndex);
        Assert.Equal(2, incident.PreviousFrameIndex);
        Assert.Equal(300, incident.ObservedGapMilliseconds);
        Assert.Contains("if transmission was strictly periodic", incident.Note);
    }

    [Fact]
    public void TimingStatisticsNeverMixCanChannelsOrElevenAndTwentyNineBitIds()
    {
        var frames = new[]
        {
            Frame(0, 0x100, 0),
            Frame(0, 0x100, 0, CanFrameFlags.Extended),
            Frame(1, 0x100, 5),
            Frame(0, 0x100, 100),
            Frame(0, 0x100, 10, CanFrameFlags.Extended),
            Frame(1, 0x100, 205),
            Frame(0, 0x100, 200),
        }.OrderBy(frame => frame.TimestampNanoseconds)
         .ThenBy(frame => frame.Channel).ToArray();

        var stats = TraceStatistics.Compute(frames);
        Assert.Equal(3, stats.Count);
        Assert.Equal(100,
            stats.Single(row => row.Channel == 0 && !row.IsExtended).AveragePeriodMilliseconds);
        Assert.Equal(10,
            stats.Single(row => row.Channel == 0 && row.IsExtended).AveragePeriodMilliseconds);
        Assert.Equal(200,
            stats.Single(row => row.Channel == 1).AveragePeriodMilliseconds);
        var db = Database();
        var cycle = DbcCycleTimingAnalyzer.Analyze(frames, stats,
            [new DbcDecoder(db)]);
        Assert.Equal(2, cycle.Rows.Count); // Extended ID has no matching DBC declaration
        Assert.Equal(0, cycle.Rows.Single(row => row.Channel == 0).LateGapCount);
        Assert.Equal(1, cycle.Rows.Single(row => row.Channel == 1).LateGapCount);
    }

    [Fact]
    public void RemoteAndErrorObjectsDoNotDistortNominalCycleTiming()
    {
        var frames = new[]
        {
            Frame(0, 0x100, 0),
            Frame(0, 0x100, 10, CanFrameFlags.Remote),
            Frame(0, 0x100, 30, CanFrameFlags.Error),
            Frame(0, 0x100, 100)
        };
        var result = Analyze(frames, [new DbcDecoder(Database())]);
        Assert.Equal(2, Assert.Single(result.Rows).RecordedFrameCount);
        Assert.Equal(100, result.Rows[0].AveragePeriodMs);
        Assert.Empty(result.Incidents);
    }

    [Fact]
    public void MatchingMultipleDecodersIsExplicitlyAmbiguousNotFirstWins()
    {
        var frames = new[] { Frame(0, 0x100, 0), Frame(0, 0x100, 350) };
        var original = Database();
        var duplicate = original with { Name = "second" };
        var result = Analyze(frames,
            [new DbcDecoder(original), new DbcDecoder(duplicate)]);
        Assert.Empty(result.Rows);
        Assert.Empty(result.Incidents);
        Assert.Equal(1, result.AmbiguousProfiles);
    }

    [Fact]
    public void PerBusDbcAssignmentExcludesOtherNetworks()
    {
        var frames = new[]
        {
            Frame(0, 0x100, 0), Frame(1, 0x100, 0),
            Frame(0, 0x100, 100), Frame(1, 0x100, 450)
        };
        var decoder = new DbcDecoder(Database(), new DbcDecoderOptions(Channel: 0));
        var result = Analyze(frames, [decoder]);
        Assert.Single(result.Rows);
        Assert.Equal(0, result.Rows[0].Channel);
        Assert.Empty(result.Incidents);
    }

    [Fact]
    public void IncidentListIsBoundedAndCountsRemainComplete()
    {
        var frames = Enumerable.Range(0, 12).Select(index =>
            Frame(0, 0x100, index * 300)).ToArray();
        var result = Analyze(frames, [new DbcDecoder(Database())], maxIncidents: 3);
        Assert.Equal(3, result.Incidents.Count);
        Assert.Equal(11, result.Rows[0].LateGapCount);
        Assert.Equal(8, result.SuppressedIncidentCount);
        Assert.Equal(22, result.Rows[0].PotentialMissingFrames);
    }

    [Fact]
    public void EventTriggeredEarlyFramesAreNotConsideredLostMessages()
    {
        var frames = new[]
        {
            Frame(0, 0x100, 0), Frame(0, 0x100, 20),
            Frame(0, 0x100, 120)
        };
        var result = Analyze(frames, [new DbcDecoder(Database())]);
        var row = Assert.Single(result.Rows);
        Assert.Equal(1, row.EarlyGapCount);
        Assert.Equal(0, row.PotentialMissingFrames);
        Assert.Empty(result.Incidents);
        Assert.Equal("Fast/event frames", row.Status);
    }

    [Fact]
    public void WithoutNominalDbcTimingNoDeviationIsAssumed()
    {
        var message = new DbcMessage(0x100, "NoNominal", 8, "ECM", false, []);
        var db = new DbcDatabase("none", new Dictionary<uint, DbcMessage> { [0x100] = message });
        var frames = new[] { Frame(0, 0x100, 0), Frame(0, 0x100, 500) };
        Assert.Empty(Analyze(frames, [new DbcDecoder(db)]).Rows);
    }
}
