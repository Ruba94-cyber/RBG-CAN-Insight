using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalRelationshipMapCsvTests
{
    private static SignalRelationshipMapReport Fixture()
    {
        var dbc = new DbcParser().Parse("review-dbc", new StringReader(
            """
            BO_ 256 Signals: 8 ECU
             SG_ EngineSpeed : 0|8@1+ (1,0) [0|255] "rpm" ECU
            BO_ 257 Inputs: 8 ECU
             SG_ DriverDemand : 0|8@1+ (1,0) [0|255] "%" ECU
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [10, 0, 0, 0, 0, 0, 0, 0], 1_000_000_000L),
            new CanFrame(0, 257, [20, 0, 0, 0, 0, 0, 0, 0], 1_100_000_000L)
        };
        var signals = SignalTimelineBuilder.Build(frames, [dbc],
            sourceAt: _ => 0, sourcePaths: ["original,recording.asc"]);
        var a = Assert.Single(signals.Where(item => item.Signal.Name == "EngineSpeed"));
        var b = Assert.Single(signals.Where(item => item.Signal.Name == "DriverDemand"));
        var observed = new SignalRelationship(a, b,
            100_000_000L, 0.83, -0.72,
            24, 12, 11, 0, 1, 1_000_000_000L, 1_100_000_000L);
        var edge = new SignalMapEdge(observed);
        var group = new SignalMapGroup(1, [a, b], [edge]);
        return new SignalRelationshipMapReport(
            0, 2, 2, 1, 1, false, false, [group], [edge]);
    }

    [Fact]
    public void CsvPreservesPhysicalLoggerOriginalFrameIndicesAndCultureNeutralTime()
    {
        var csv = SignalRelationshipMapCsv.Export(Fixture());
        Assert.Contains("logger_index_1based,group_number", csv);
        Assert.Contains("source_frame_a_1based,source_frame_b_1based", csv);
        Assert.Contains("source_file_a,source_file_b", csv);
        Assert.Contains("\"original,recording.asc\"", csv);
        Assert.Contains("1000000000,1100000000", csv);
        Assert.Contains(",0.83,-0.72,24,12,11,100,1,2,1000000000,1100000000,", csv);
        Assert.Contains("not a verified ECU dependency or cause", csv);
        Assert.Equal(2, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void UnverifiedOrEmptyMapExportsOnlySchemaNotFabricatedEdges()
    {
        var empty = new SignalRelationshipMapReport(2, 0, 0, 0, 0,
            false, false, [], []);
        var csv = SignalRelationshipMapCsv.Export(empty);
        Assert.Single(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("total_detected_links", csv);
    }

    [Fact]
    public void DbcControlledLabelStartingWithExcelFormulaIsNeutralized()
    {
        var report = Fixture();
        var original = Assert.Single(report.Edges);
        var message = new DbcMessage(256, "=HYPERLINK(\"https://invalid.example\")",
            8, "ECU", false, [original.A.Signal]);
        var malicious = new SignalTimeline(message, original.A.Signal,
            "review-dbc", channel: 0, originalSourceIndex: 0,
            originalSourcePath: "original,recording.asc");
        var rewritten = original.Evidence with { Reference = malicious };
        var revisedEdge = new SignalMapEdge(rewritten);
        var revisedGroup = new SignalMapGroup(1,
            [malicious, original.B], [revisedEdge]);
        var revised = report with
        {
            Groups = [revisedGroup],
            Edges = [revisedEdge]
        };
        var csv = SignalRelationshipMapCsv.Export(revised);
        Assert.Contains("\"'=HYPERLINK(\"\"https://invalid.example\"\").EngineSpeed\"", csv);
        Assert.DoesNotContain("\"=HYPERLINK", csv);
    }
}
