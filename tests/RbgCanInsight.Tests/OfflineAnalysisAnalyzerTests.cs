using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OfflineAnalysisAnalyzerTests
{
    [Fact]
    public void CombinesDiagnosticTimingSignalAndCatalogFindingsWithSourceEvidence()
    {
        var database = new DbcParser().Parse("fixture", new StringReader(
            "BO_ 256 Engine: 8 ECM\n SG_ Temperature : 0|8@1+ (1,0) [0|100] \"degC\" Vector__XXX\n"));
        var signalFrames = new[]
        {
            Frame(0x100, [20, 0, 0, 0, 0, 0, 0, 0], 0),
            Frame(0x100, [150, 0, 0, 0, 0, 0, 0, 0], 1_000_000)
        };
        var timelines = SignalTimelineBuilder.Build(signalFrames, [database]);
        var reports = SignalChangeAnalyzer.Analyze(timelines, [database], new HashSet<uint> { 0x100 });
        var diagnosticFrames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0x12, 0x34, 0, 0, 0, 0], 2_000_000),
            Frame(0x7E8, [0x03, 0x7F, 0x22, 0x31, 0, 0, 0, 0], 3_000_000)
        };
        var insight = DiagnosticInsightAnalyzer.Analyze(UdsTransactionAnalyzer.Analyze(diagnosticFrames), []);
        var timing = new TimingIncident(0, 0x100, false, 5_000_000, 1, 50, 5, 10);

        var result = OfflineAnalysisAnalyzer.Analyze(reports, [timing], insight, []);

        Assert.Contains(result.Findings, finding =>
            finding.Category == OfflineFindingCategory.Diagnostic && finding.FrameIndices.Count > 0);
        Assert.Contains(result.Findings, finding =>
            finding.Category == OfflineFindingCategory.Timing && finding.Severity == OfflineFindingSeverity.Critical);
        Assert.Contains(result.Findings, finding =>
            finding.Category == OfflineFindingCategory.Signal && finding.Title.Contains("Temperature"));
        Assert.Contains(result.Findings, finding =>
            finding.Category == OfflineFindingCategory.Catalog && finding.Details.Contains("0x1234"));
    }

    [Fact]
    public void NominalDbcLateGapCreatesOnlySourceLinkedAdvisoryNotUnprovenPacketLoss()
    {
        var database = new DbcParser().Parse("DBC", new StringReader(
            """
            BO_ 256 Engine: 8 ECM
             SG_ State : 0|8@1+ (1,0) [0|255] "" ECM
            BA_ "GenMsgCycleTime" BO_ 256 100;
            """));
        var frames = new[]
        {
            Frame(0x100, [1,0,0,0,0,0,0,0], 0),
            Frame(0x100, [1,0,0,0,0,0,0,0], 100_000_000),
            Frame(0x100, [1,0,0,0,0,0,0,0], 500_000_000)
        };
        var cycle = DbcCycleTimingAnalyzer.Analyze(frames, TraceStatistics.Compute(frames),
            [new DbcDecoder(database)]);
        var result = OfflineAnalysisAnalyzer.Analyze([], [], DiagnosticInsightSnapshot.Empty, [],
            dbcCycleTiming: cycle);
        var finding = Assert.Single(result.Findings, item =>
            item.Title.Contains("DBC nominal cycle deviation"));
        Assert.Equal(OfflineFindingCategory.Timing, finding.Category);
        Assert.Equal(2, Assert.Single(finding.FrameIndices));
        Assert.Equal(500_000_000L, finding.TimestampNanoseconds);
        Assert.Contains("not proof of lost packets", finding.Details);
    }

    [Fact]
    public void BuildsRecordedSequenceEcuSnapshotAndHonestCatalogCoverage()
    {
        var did = new DidParameter(0xF190, "VIN", "Vehicle identification number", "", "", null, null,
            null, null, "A_ASCIISTRING", 32, "ECM", "fixture");
        var service = new DiagnosticServiceDefinition(0x22, null, 0xF190, "ReadDataByIdentifier",
            "Read data by identifier", "", "ECM", "fixture");
        var catalog = new DidCatalog("fixture", "ECM", "fixture", [did], Services: [service]);
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0], 0),
            Frame(0x7E8, [0x07, 0x62, 0xF1, 0x90, 0x41, 0x42, 0x43, 0x44], 1_000_000),
            Frame(0x7E0, [0x02, 0x10, 0x03, 0, 0, 0, 0, 0], 2_000_000),
            Frame(0x7E8, [0x02, 0x50, 0x03, 0, 0, 0, 0, 0], 3_000_000)
        };
        var insight = DiagnosticInsightAnalyzer.Analyze(UdsTransactionAnalyzer.Analyze(frames, [catalog]), [catalog]);

        var result = OfflineAnalysisAnalyzer.Analyze([], [], insight, [catalog]);

        Assert.Equal(2, result.Sequence.Count);
        Assert.Contains(result.Sequence, item => item.Category == "Session");
        var snapshot = Assert.Single(result.EcuSnapshot);
        Assert.Equal(0xF190, snapshot.Did);
        Assert.Contains("ABCD", snapshot.Value);
        var didCoverage = Assert.Single(result.CatalogCoverage, row => row.Entity == "DIDs");
        Assert.Equal(1, didCoverage.Observed);
        Assert.Equal(1, didCoverage.Resolved);
        var serviceCoverage = Assert.Single(result.CatalogCoverage, row => row.Entity == "Services");
        Assert.Equal(2, serviceCoverage.Observed);
        Assert.Equal(1, serviceCoverage.Resolved);
        Assert.Contains("0x10", serviceCoverage.UnknownItems);
    }

    private static CanFrame Frame(uint id, byte[] data, long timestamp) =>
        new(0, id, data, timestamp, CanFrameFlags.None);
}
