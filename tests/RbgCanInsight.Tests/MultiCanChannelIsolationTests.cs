using RbgCanInsight.Analysis;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class MultiCanChannelIsolationTests
{
    private static DbcDatabase Database() => new DbcParser().Parse(
        "vehicle", new StringReader(
            """
            BO_ 256 EngineData: 8 ECM
             SG_ Temp : 0|8@1+ (1,0) [0|255] "degC" ECM
             SG_ EngineMode : 8|8@1+ (1,0) [0|255] "" ECM
            """));

    private static CanFrame Frame(int channel, int value, long timestamp, bool shortFrame = false) =>
        new(channel, 0x100, shortFrame ? [(byte)value] : [(byte)value, 1, 0, 0, 0, 0, 0, 0], timestamp);

    private static CorrelationCandidate Candidate(DbcDatabase db) => new(
        new DidParameter(0xF405, "EngineTemperature", "Engine temperature",
            "Current temperature", "degC", 0, 255, 1, 0, "A_UINT8", 8,
            "ECM", "test.cdd"),
        db.Messages[256],
        db.Messages[256].Signals.First(signal => signal.Name == "Temp"),
        db.Name, 0.88, ["unit match"]);

    private static DiagnosticDidSample DidSample(int channel, double value, int sequence) => new(
        0xF405, "EngineTemperature", "EngineTemperature", value, value.ToString(),
        "degC", sequence * 1_000_000_000L, sequence, "ECM", "Recorded", channel);

    private static TimeCorrelationOptions NoLag => new(
        MaxCandidates: 1, MaximumLagMilliseconds: 0,
        MaximumPairDistanceMilliseconds: 1, MinimumPairsForCandidate: 3,
        MinimumPairsForConfirmation: 5);

    [Fact]
    public void SameMessageAndSignalOnIndependentCanChannelsHasSeparateTimelines()
    {
        var db = Database();
        var frames = new[]
        {
            Frame(0, 10, 0), Frame(1, 200, 0),
            Frame(0, 11, 1_000_000_000), Frame(1, 201, 1_000_000_000)
        };
        var timelines = SignalTimelineBuilder.Build(frames, [db]);
        Assert.Equal(4, timelines.Count); // 2 signals x 2 CAN channels

        var temps = timelines.Where(t => t.Signal.Name == "Temp").ToArray();
        Assert.Equal(2, temps.Length);
        Assert.Equal(2, temps.Select(t => t.Key).Distinct().Count());
        Assert.Equal(new[] { 10d, 11d },
            temps.Single(t => t.Channel == 0).Samples.Select(sample => sample.Value));
        Assert.Equal(new[] { 200d, 201d },
            temps.Single(t => t.Channel == 1).Samples.Select(sample => sample.Value));
    }

    [Fact]
    public void TimeCorrelationUsesOnlyTheUdsReplyChannel()
    {
        var db = Database();
        var frames = Enumerable.Range(0, 6).SelectMany(i => new[]
        {
            Frame(0, i + 50, i * 1_000_000_000L),
            Frame(1, i + 150, i * 1_000_000_000L)
        }).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [db]);
        var samples = Enumerable.Range(0, 6)
            .Select(i => DidSample(1, i + 150, i)).ToArray();
        var answer = Assert.Single(UdsCanTimeCorrelation.Correlate(
            samples, [Candidate(db)], timelines, NoLag));

        Assert.Equal(6, answer.PairCount);
        Assert.All(answer.Points, point => Assert.InRange(point.SignalValue, 150, 155));
    }

    [Fact]
    public void UnknownDiagnosticChannelMustNotArbitrarilySelectOneCanBus()
    {
        var db = Database();
        var frames = new[]
        {
            Frame(0, 1, 0), Frame(1, 2, 0),
            Frame(0, 3, 1_000_000_000), Frame(1, 4, 1_000_000_000)
        };
        var samples = new[]
        {
            DidSample(-1, 2, 0), DidSample(-1, 4, 1)
        };
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            samples, [Candidate(db)], SignalTimelineBuilder.Build(frames, [db]), NoLag));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Contains("Ambiguous", result.Explanation);
    }

    [Fact]
    public void ShortMessageReportsMissingSignalOnThatCanBusOnly()
    {
        var db = Database();
        var frames = new[]
        {
            Frame(0, 10, 0),
            Frame(1, 11, 1_000_000, shortFrame: true)
        };
        var timelines = SignalTimelineBuilder.Build(frames, [db]);
        var observed = frames.Select(frame => (frame.Channel, frame.Identifier, frame.IsExtended))
            .ToHashSet();
        var reports = SignalChangeAnalyzer.Analyze(timelines, [db], 
            frames.Select(frame => frame.Identifier).ToHashSet(),
            seenFrameIdentities: observed);
        Assert.Contains(reports, report => report.Timeline.Channel == 1 &&
            report.SignalName == "EngineMode" && report.Activity == SignalActivity.Missing);
        Assert.DoesNotContain(reports, report => report.Timeline.Channel == 0 &&
            report.SignalName == "EngineMode" && report.Activity == SignalActivity.Missing);
    }

    [Fact]
    public void SampleAtNeverReportsAValueBeforeItsFirstRealCanFrame()
    {
        var db = Database();
        var timelines = SignalTimelineBuilder.Build(
            [Frame(0, 25, 100_000_000), Frame(0, 26, 200_000_000)], [db]);
        var temp = Assert.Single(timelines, timeline => timeline.Signal.Name == "Temp");
        Assert.Null(temp.SampleAt(99_999_999));
        Assert.Equal(25d, temp.SampleAt(150_000_000)!.Value.Value);
        Assert.Equal(26d, temp.SampleAt(200_000_000)!.Value.Value);
    }

    [Fact]
    public void DbcAssignedToCanOneDoesNotReportCanTwoMissingSignal()
    {
        var db = Database();
        var decoder = new DbcDecoder(db, new DbcDecoderOptions(Channel: 0));
        var frames = new[]
        {
            Frame(0, 10, 0),
            Frame(1, 11, 1_000_000, shortFrame: true)
        };
        var timelines = SignalTimelineBuilder.Build(frames, [decoder]);
        var observed = frames.Select(frame => (frame.Channel, frame.Identifier, frame.IsExtended))
            .ToHashSet();
        var reports = SignalChangeAnalyzer.Analyze(timelines, [db],
            frames.Select(frame => frame.Identifier).ToHashSet(),
            seenFrameIdentities: observed, activeDecoders: [decoder]);
        Assert.DoesNotContain(reports, report => report.Timeline.Channel == 1);
        Assert.Contains(reports, report => report.Timeline.Channel == 0 &&
            report.SignalName == "EngineMode" && report.SampleCount == 1);
    }

    [Fact]
    public void InactiveMultiplexBranchIsNotReportedMissing()
    {
        const string dbc =
            """
            BO_ 256 Multiplexed: 8 ECM
             SG_ Switch M : 0|8@1+ (1,0) [0|255] "" ECM
             SG_ Always : 8|8@1+ (1,0) [0|255] "" ECM
             SG_ BranchA m1 : 16|8@1+ (1,0) [0|255] "" ECM
            """;
        var db = new DbcParser().Parse("mux", new StringReader(dbc));
        var frame = new CanFrame(0, 256, [0, 23, 99, 0, 0, 0, 0, 0], 0);
        var timelines = SignalTimelineBuilder.Build([frame], [db]);
        var reports = SignalChangeAnalyzer.Analyze(timelines, [db],
            new HashSet<uint> { 256 },
            seenFrameIdentities: new HashSet<(int Channel, uint Identifier, bool Extended)>
            {
                (0, 256u, false)
            });
        Assert.DoesNotContain(reports, item =>
            item.SignalName == "BranchA" && item.Activity == SignalActivity.Missing);
        Assert.Contains(reports, item =>
            item.SignalName == "Switch" && item.SampleCount == 1);
    }

    [Fact]
    public void ReadDataByIdentifierValuesRememberTheirPhysicalBus()
    {
        var request = new CanFrame(1, 0x7E0, [0x03, 0x22, 0xF4, 0x05], 0);
        var response = new CanFrame(1, 0x7E8, [0x04, 0x62, 0xF4, 0x05, 0xA0], 10_000_000);
        var transactions = UdsTransactionAnalyzer.Analyze([request, response]);
        var insight = DiagnosticInsightAnalyzer.Analyze(transactions, []);
        var value = Assert.Single(insight.DidSamples);
        Assert.Equal(1, value.Channel);
    }
}
