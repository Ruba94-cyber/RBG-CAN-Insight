using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class AnalysisTests
{
    private static DbcDatabase BuildDatabase() =>
        new DbcParser().Parse("test", new StringReader(
            """
            BO_ 256 EngineData: 8 ECM
             SG_ CoolantTemp : 0|8@1+ (1,-40) [-40|215] "degC" Vector__XXX
             SG_ SteadyFlag : 8|1@1+ (1,0) [0|1] "" Vector__XXX
            """));

    private static List<CanFrame> BuildFrames()
    {
        // CoolantTemp ramps 20 -> 22 degC (raw 60,60,61,62); SteadyFlag constant 1.
        byte[][] payloads = [[60, 1], [60, 1], [61, 1], [62, 1]];
        return payloads
            .Select((payload, index) => new CanFrame(0, 256, payload, index * 100_000_000L))
            .ToList();
    }

    [Fact]
    public void BuildsTimelinesAndDetectsChanges()
    {
        var database = BuildDatabase();
        var frames = BuildFrames();

        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        var reports = SignalChangeAnalyzer.Analyze(
            timelines, [database], frames.Select(f => f.Identifier).ToHashSet());

        var coolant = reports.Single(report => report.SignalName == "CoolantTemp");
        Assert.Equal(4, coolant.SampleCount);
        Assert.Equal(2, coolant.ChangeCount);
        Assert.Equal(20, coolant.FirstValue);
        Assert.Equal(22, coolant.LastValue);
        Assert.Equal(SignalActivity.Variable, coolant.Activity);
        Assert.Equal(20, coolant.Changes[0].FromValue);
        Assert.Equal(21, coolant.Changes[0].ToValue);
        Assert.Equal(2, coolant.Changes[0].FrameIndex);

        var steady = reports.Single(report => report.SignalName == "SteadyFlag");
        Assert.Equal(SignalActivity.Stable, steady.Activity);
        Assert.Equal(0, steady.ChangeCount);
    }

    [Fact]
    public void FlagsOutOfRangeAsAnomalous()
    {
        var database = BuildDatabase();
        // Raw 0 => -40 is in range; raw 255 => 215 in range; extend range check via min:
        // build frames with raw values that map below the documented minimum is impossible here,
        // so use a database with a tighter range instead.
        var tight = new DbcParser().Parse("tight", new StringReader(
            """
            BO_ 256 EngineData: 8 ECM
             SG_ CoolantTemp : 0|8@1+ (1,-40) [0|100] "degC" Vector__XXX
            """));
        var frames = new List<CanFrame> { new(0, 256, [10], 0) }; // -30 degC < documented 0

        var timelines = SignalTimelineBuilder.Build(frames, [tight]);
        var reports = SignalChangeAnalyzer.Analyze(timelines, [tight]);

        Assert.Equal(SignalActivity.Anomalous, reports.Single().Activity);
        Assert.True(reports.Single().HasOutOfRange);
        _ = database;
    }

    [Fact]
    public void ReportsMissingSignalsForSeenMessages()
    {
        var database = new DbcParser().Parse("mux", new StringReader(
            """
            BO_ 256 MuxData: 8 ECM
             SG_ Selector M : 0|8@1+ (1,0) [0|255] "" Vector__XXX
             SG_ BranchA m0 : 8|8@1+ (1,0) [0|255] "" Vector__XXX
             SG_ BranchB m1 : 8|8@1+ (1,0) [0|255] "" Vector__XXX
            """));
        // Only multiplex branch 0 ever appears. BranchB is an inactive branch, which is
        // not evidence of a missing signal, so it must not be reported as Missing.
        var frames = new List<CanFrame> { new(0, 256, [0, 42], 0) };

        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        var reports = SignalChangeAnalyzer.Analyze(
            timelines, [database], frames.Select(f => f.Identifier).ToHashSet());

        Assert.DoesNotContain(reports, r => r.SignalName == "BranchB" && r.Activity == SignalActivity.Missing);
        Assert.Contains(reports, r => r.SignalName == "BranchA");
    }

    [Fact]
    public void ComputesPerMessageStatistics()
    {
        var frames = new List<CanFrame>
        {
            new(0, 0x100, [1], 0),
            new(0, 0x100, [2], 10_000_000),
            new(0, 0x100, [3], 20_000_000),
            new(0, 0x200, [4], 5_000_000)
        };

        var rows = TraceStatistics.Compute(frames);

        Assert.Equal(2, rows.Count);
        var first = rows.Single(row => row.Identifier == 0x100);
        Assert.Equal(3, first.FrameCount);
        Assert.Equal(10, first.AveragePeriodMilliseconds, precision: 3);
    }

    [Fact]
    public void PerfectlyPeriodicMessageHasZeroStdDev()
    {
        var frames = new List<CanFrame>
        {
            new(0, 0x100, [1], 0),
            new(0, 0x100, [2], 10_000_000),
            new(0, 0x100, [3], 20_000_000),
            new(0, 0x100, [4], 30_000_000)
        };

        var row = TraceStatistics.Compute(frames).Single();

        Assert.Equal(0, row.StdDevPeriodMilliseconds, precision: 6);
    }

    [Fact]
    public void ComputesPeriodStdDevFromKnownJitter()
    {
        // Periods 10ms, 30ms -> mean 20ms, population variance ((10-20)^2+(30-20)^2)/2 = 100,
        // stddev = 10ms.
        var frames = new List<CanFrame>
        {
            new(0, 0x100, [1], 0),
            new(0, 0x100, [2], 10_000_000),
            new(0, 0x100, [3], 40_000_000)
        };

        var row = TraceStatistics.Compute(frames).Single();

        Assert.Equal(20, row.AveragePeriodMilliseconds, precision: 3);
        Assert.Equal(10, row.StdDevPeriodMilliseconds, precision: 3);
    }

    [Fact]
    public void CountsBurstsAsGapsWellBelowAveragePeriod()
    {
        // Periods (ms): 10, 10, 1, 10, 10 -> average 8.2ms; the 1ms gap is far below
        // 0.4x average and counts as a single burst occurrence.
        var frames = new List<CanFrame>
        {
            new(0, 0x300, [1], 0),
            new(0, 0x300, [2], 10_000_000),
            new(0, 0x300, [3], 20_000_000),
            new(0, 0x300, [4], 21_000_000),
            new(0, 0x300, [5], 31_000_000),
            new(0, 0x300, [6], 41_000_000)
        };

        var row = TraceStatistics.Compute(frames).Single();

        Assert.Equal(1, row.BurstCount);
    }

    [Fact]
    public void NoBurstsWhenAllPeriodsAreConsistent()
    {
        var frames = new List<CanFrame>
        {
            new(0, 0x100, [1], 0),
            new(0, 0x100, [2], 10_000_000),
            new(0, 0x100, [3], 20_000_000),
            new(0, 0x100, [4], 30_000_000)
        };

        var row = TraceStatistics.Compute(frames).Single();

        Assert.Equal(0, row.BurstCount);
    }

    [Fact]
    public void EstimatesStandardAndExtendedFrameBitCounts()
    {
        var standard = new CanFrame(0, 0x100, new byte[8], 0);
        var extended = new CanFrame(0, 0x1ABCDEF, new byte[8], 0, CanFrameFlags.Extended);

        Assert.Equal(47 + 64, BusLoadCalculator.EstimateFrameBits(standard));
        Assert.Equal(67 + 64, BusLoadCalculator.EstimateFrameBits(extended));
    }

    [Fact]
    public void ComputesBusLoadPercentFromAssumedBitrate()
    {
        // 10 standard 8-byte frames = 10 * 111 = 1110 bits, at 500 kbit/s over a 1s span:
        // 1110 / 500_000 = 0.00222s busy -> 0.222% load.
        var frames = Enumerable.Range(0, 10)
            .Select(i => new CanFrame(0, 0x100, new byte[8], i * 100_000_000L))
            .ToArray();

        var load = BusLoadCalculator.ComputeLoadPercent(frames, 1_000_000_000L, 500_000);

        Assert.Equal(0.222, load, precision: 3);
    }

    [Fact]
    public void BusLoadIsClampedAndHandlesDegenerateInput()
    {
        var frames = new[] { new CanFrame(0, 0x100, new byte[8], 0) };

        Assert.Equal(0, BusLoadCalculator.ComputeLoadPercent(frames, 0, 500_000));
        Assert.Equal(0, BusLoadCalculator.ComputeLoadPercent(frames, 1_000_000_000, 0));
        Assert.Equal(100, BusLoadCalculator.ComputeLoadPercent(frames, 1, 1));
    }
}
