using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceRegressionAnalyzerTests
{
    private static MessageStatisticsRow Row(
        uint id, long frames, double period, double stdDev = 0.1, int channel = 0, bool extended = false,
        int minLen = 8, int maxLen = 8, int bursts = 0) =>
        new(channel, id, extended, frames, 0, 1_000_000_000, period, period, period, minLen, maxLen, stdDev, bursts);

    private static IReadOnlyList<RegressionFinding> Run(
        MessageStatisticsRow[] baseline, MessageStatisticsRow[] candidate, RegressionOptions? options = null) =>
        TraceRegressionAnalyzer.Compare(baseline, candidate, options: options);

    [Fact]
    public void IdenticalTracesProduceNoFindings()
    {
        var rows = new[] { Row(0x100, 100, 10), Row(0x200, 50, 20) };
        Assert.Empty(Run(rows, rows));
    }

    [Fact]
    public void AppearedAndDisappearedIdentifiersAreReportedPerChannelAndFormat()
    {
        var findings = Run(
            [Row(0x100, 100, 10), Row(0x100, 100, 10, extended: true)],
            [Row(0x100, 100, 10), Row(0x300, 5, 10)]);
        Assert.Contains(findings, f => f.Summary.Contains("missing from the candidate") && f.Subject == "CH1 00000100x");
        Assert.Contains(findings, f => f.Summary.Contains("only in the candidate") && f.Subject == "CH1 300");
        Assert.DoesNotContain(findings, f => f.Subject == "CH1 100");
    }

    [Fact]
    public void PeriodChangeBeyondToleranceIsReportedAndSmallChangeIsNot()
    {
        Assert.Empty(Run([Row(0x100, 100, 10)], [Row(0x100, 100, 11.5)]));
        var finding = Assert.Single(Run([Row(0x100, 100, 10)], [Row(0x100, 100, 15)]));
        Assert.Equal("Timing", finding.Category);
        Assert.Contains("increased by 50 %", finding.Summary.Replace(" ", " ").Replace(" ", " "));
        Assert.Equal("10.00 ms", finding.Baseline);
        Assert.Equal("15.00 ms", finding.Candidate);
    }

    [Fact]
    public void ToleranceIsConfigurable()
    {
        var findings = Run([Row(0x100, 100, 10)], [Row(0x100, 100, 11.5)], new RegressionOptions(PeriodTolerance: 0.1));
        Assert.Single(findings);
    }

    [Fact]
    public void JitterIncreaseNeedsBothRelativeAndAbsoluteGrowth()
    {
        Assert.Empty(Run([Row(0x100, 100, 10, stdDev: 0.1)], [Row(0x100, 100, 10, stdDev: 0.4)])); // < 1 ms absolute
        Assert.Empty(Run([Row(0x100, 100, 10, stdDev: 3)], [Row(0x100, 100, 10, stdDev: 4.5)]));   // < 2x
        var finding = Assert.Single(Run([Row(0x100, 100, 10, stdDev: 0.5)], [Row(0x100, 100, 10, stdDev: 3)]));
        Assert.Equal("Period jitter increased", finding.Summary);
    }

    [Fact]
    public void RarelySeenIdentifiersAreNotComparedByTiming()
    {
        Assert.Empty(Run([Row(0x100, 2, 10)], [Row(0x100, 2, 500)]));
    }

    [Fact]
    public void PayloadLengthChangeIsReported()
    {
        var finding = Assert.Single(Run([Row(0x100, 100, 10)], [Row(0x100, 100, 10, minLen: 6, maxLen: 8)]));
        Assert.Equal("Payload length changed", finding.Summary);
        Assert.Equal("8 bytes", finding.Baseline);
        Assert.Equal("6-8 bytes", finding.Candidate);
    }

    [Fact]
    public void ExtraBurstsAreInformational()
    {
        var finding = Assert.Single(Run([Row(0x100, 100, 10, bursts: 1)], [Row(0x100, 100, 10, bursts: 4)]));
        Assert.Equal(RegressionSeverity.Info, finding.Severity);
    }

    [Fact]
    public void FindingsAreOrderedWarningsFirstThenByCategory()
    {
        var findings = Run(
            [Row(0x100, 100, 10, bursts: 0), Row(0x200, 100, 10)],
            [Row(0x100, 100, 10, bursts: 3), Row(0x300, 4, 10)]);
        Assert.Equal(RegressionSeverity.Warning, findings[0].Severity);
        Assert.Equal(RegressionSeverity.Info, findings[^1].Severity);
    }

    private static IReadOnlyList<SignalChangeReport> Signals(byte[][] payloads)
    {
        var database = new DbcParser().Parse("t", new StringReader(
            """
            BO_ 256 Engine: 8 ECM
             SG_ Temp : 0|8@1+ (1,0) [0|100] "C" Vector__XXX
            """));
        var decoder = new DbcDecoder(database);
        var frames = payloads.Select((payload, i) => new CanFrame(0, 256, payload, i * 10_000_000L)).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [decoder]);
        return SignalChangeAnalyzer.Analyze(timelines, [database],
            frames.Select(f => f.Identifier).ToHashSet(),
            seenFrameIdentities: frames.Select(f => (f.Channel, f.Identifier, f.IsExtended)).ToHashSet(),
            activeDecoders: [decoder]);
    }

    [Fact]
    public void SignalStatusTransitionsAndRangeViolationsAreReported()
    {
        var stable = Signals([[10], [10], [10]]);
        var variable = Signals([[10], [20], [30]]);
        var outOfRange = Signals([[10], [200], [30]]);

        Assert.Empty(TraceRegressionAnalyzer.Compare([], [], stable, stable));

        var change = Assert.Single(TraceRegressionAnalyzer.Compare([], [], stable, variable));
        Assert.Equal("Signal", change.Category);
        Assert.Equal("Signal status changed", change.Summary);
        Assert.Equal("Stable", change.Baseline);
        Assert.Equal("Variable", change.Candidate);
        Assert.Equal(RegressionSeverity.Info, change.Severity);

        var worse = Assert.Single(TraceRegressionAnalyzer.Compare([], [], variable, outOfRange));
        Assert.Equal(RegressionSeverity.Warning, worse.Severity);
        Assert.Equal("Anomalous", worse.Candidate);
    }

    private static DiagnosticServiceStatistic Service(
        byte id, int requests, int positive, int negative, int missing, double? average = 10) =>
        new(id, "ReadDataByIdentifier", requests, positive, negative, missing, average, average);

    private static FaultMemoryEntry Fault(string ecu, int code) =>
        new(code, $"0x{code:X6}", "Test fault", "", ecu, 0, 0, 1, 0x09, "confirmed", false, null, []);

    private static DiagnosticInsightSnapshot Snapshot(
        DiagnosticServiceStatistic[] services, FaultMemoryEntry[]? faults = null) =>
        new([], [], faults ?? [], [], services, []);

    [Fact]
    public void DiagnosticRegressionsAreReported()
    {
        var baseline = Snapshot([Service(0x22, 10, 10, 0, 0), Service(0x19, 2, 2, 0, 0)], [Fault("ECM", 0x123456)]);
        var candidate = Snapshot(
            [Service(0x22, 10, 7, 3, 1, average: 30), Service(0x31, 1, 1, 0, 0)],
            [Fault("ECM", 0x123456), Fault("BCM", 0xABCDEF)]);

        var findings = TraceRegressionAnalyzer.Compare([], [], null, null, baseline, candidate);
        Assert.Contains(findings, f => f.Summary == "More negative responses" && f.Subject.StartsWith("0x22"));
        Assert.Contains(findings, f => f.Summary == "More requests without a response");
        Assert.Contains(findings, f => f.Summary == "Average response time increased");
        Assert.Contains(findings, f => f.Summary == "Service is missing from the candidate trace" && f.Subject.StartsWith("0x19"));
        Assert.Contains(findings, f => f.Summary == "Service appears only in the candidate trace" && f.Subject.StartsWith("0x31"));
        Assert.Contains(findings, f => f.Summary == "Fault code appears only in the candidate trace" && f.Subject.StartsWith("BCM"));
        Assert.DoesNotContain(findings, f => f.Subject.Contains("123456"));
    }
}
