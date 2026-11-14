using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class EvidenceKindTests
{
    private static DbcDatabase Database() => new DbcParser().Parse("evidence-dbc", new StringReader(
        """
        BO_ 256 Signals: 8 ECU
         SG_ EngineSpeed : 0|8@1+ (1,0) [0|255] "rpm" ECU
        BO_ 257 Inputs: 8 ECU
         SG_ DriverDemand : 0|8@1+ (1,0) [0|255] "%" ECU
        """));

    private static CorrelationCandidate Candidate(DbcDatabase database) => new(
        new DidParameter(0xF405, "Temperature", "Temperature", "Coolant temperature",
            "degC", 0, 255, 1, 0, "A_UINT8", 8, "ECM", "fixture.cdd"),
        database.Messages[256], database.Messages[256].Signals[0],
        database.Name, 0.88, ["matching unit and name"]);

    private static UdsCanCorrelationResult Result(CorrelationVerdict verdict)
    {
        var database = Database();
        return new UdsCanCorrelationResult(Candidate(database), verdict, .8, .8, .8, 2, 1, 0,
            1, 1, 1, 0, 0, 0, "recorded",
            [new CorrelationPoint(0, 0, 10, 10, 10, 0, 0, 0)])
        {
            OriginalSourceIndex = 0, SignalChannel = 0, SignalSourceIdentifier = 256, SignalSourceIsExtended = false
        };
    }

    private static SignalRelationshipMapReport MapFixture()
    {
        var dbc = Database();
        var frames = new[]
        {
            new CanFrame(0, 256, [10, 0, 0, 0, 0, 0, 0, 0], 1_000_000_000L),
            new CanFrame(0, 257, [20, 0, 0, 0, 0, 0, 0, 0], 1_100_000_000L)
        };
        var signals = SignalTimelineBuilder.Build(frames, [dbc], sourceAt: _ => 0, sourcePaths: ["rec.asc"]);
        var a = signals.Single(s => s.Signal.Name == "EngineSpeed");
        var b = signals.Single(s => s.Signal.Name == "DriverDemand");
        var edge = new SignalMapEdge(new SignalRelationship(a, b, 100_000_000L, 0.83, -0.72, 24, 12, 11, 0, 1,
            1_000_000_000L, 1_100_000_000L));
        return new SignalRelationshipMapReport(0, 2, 2, 1, 1, false, false, [new SignalMapGroup(1, [a, b], [edge])], [edge]);
    }

    [Fact]
    public void LabelsAndDescriptionsExistForEveryKind()
    {
        foreach (var kind in Enum.GetValues<EvidenceKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(EvidenceKinds.Label(kind)));
            Assert.False(string.IsNullOrWhiteSpace(EvidenceKinds.Description(kind)));
            Assert.Contains(EvidenceKinds.Label(kind), EvidenceKinds.Legend);
        }
        Assert.Equal(4, Enum.GetValues<EvidenceKind>().Length);
    }

    [Fact]
    public void SignalAssociationsAreHypothesesAndNeverConfirmed()
    {
        var edge = MapFixture().Edges.Single();
        Assert.Equal(EvidenceKind.Hypothesis, EvidenceClassification.For(edge));
        Assert.Equal(EvidenceKind.Hypothesis, EvidenceClassification.For(edge.Evidence));
        Assert.NotEqual(EvidenceKind.Confirmed, EvidenceClassification.For(edge.Evidence));
    }

    [Theory]
    [InlineData(CorrelationVerdict.Candidate, EvidenceKind.Hypothesis)]
    [InlineData(CorrelationVerdict.StrongCandidate, EvidenceKind.Hypothesis)]
    [InlineData(CorrelationVerdict.Rejected, EvidenceKind.Calculated)]
    [InlineData(CorrelationVerdict.InsufficientData, EvidenceKind.Calculated)]
    [InlineData(CorrelationVerdict.Confirmed, EvidenceKind.Confirmed)]
    public void UdsCanVerdictsMapToTheRightEvidenceKind(CorrelationVerdict verdict, EvidenceKind expected) =>
        Assert.Equal(expected, CorrelationEvidenceKinds.For(Result(verdict)));

    [Fact]
    public void ConfirmedIsOnlyReachedThroughTheConfirmedVerdict()
    {
        foreach (var verdict in Enum.GetValues<CorrelationVerdict>().Where(v => v != CorrelationVerdict.Confirmed))
            Assert.NotEqual(EvidenceKind.Confirmed, CorrelationEvidenceKinds.For(Result(verdict)));
    }

    [Fact]
    public void MapCsvHasEvidenceClassColumnWithHypothesisOnEveryRow()
    {
        var lines = SignalRelationshipMapCsv.Export(MapFixture()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();
        var header = lines[0].Split(',');
        Assert.Contains("evidence_class", header);
        Assert.Equal("evidence_class", header[^1]);
        Assert.EndsWith("\"Hypothesis\"", lines[1]);
    }

    [Fact]
    public void UdsCsvHasEvidenceKindColumnAsLastCell()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(1, CorrelationEvidenceCsvExporter.Write(writer, Result(CorrelationVerdict.StrongCandidate)));
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.EndsWith(",EvidenceKind", lines[0]);
        Assert.EndsWith(",Hypothesis", lines[1]);
    }

    [Fact]
    public void UdsCsvNeverReportsConfirmedForAutomaticCandidates()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        CorrelationEvidenceCsvExporter.Write(writer, Result(CorrelationVerdict.Candidate));
        Assert.DoesNotContain("Confirmed", writer.ToString().Split('\n')[1]);
    }
}
