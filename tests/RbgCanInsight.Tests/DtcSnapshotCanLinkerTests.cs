using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DtcSnapshotCanLinkerTests
{
    private static UdsCanCorrelationResult Candidate(int source, int did,
        CorrelationVerdict verdict = CorrelationVerdict.Candidate, int pairs = 2,
        bool hasPhysicalChannel = true, string ecu = "ECU")
    {
        var dbc = new DbcParser().Parse("engine", new StringReader(
            """
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 0|8@1+ (1,0) [0|255] "km/h" ECU
            """));
        var definition = new DidParameter(did, "Speed", "Speed", "",
            "km/h", 0, 255, 1, 0, "A_UINT8", 8, ecu, "fixture.cdd");
        var candidate = new CorrelationCandidate(definition, dbc.Messages[256],
            dbc.Messages[256].Signals[0], dbc.Name, .9, ["name and unit"]);
        return new UdsCanCorrelationResult(candidate, verdict, .9, .9, .9, pairs,
            1, 0, 1, 1, 1, 0, 0, 0, "source-backed values",
            [new CorrelationPoint(10, 10, 10, 10, 10, 0, 3, 4)])
        {
            OriginalSourceIndex = source,
            SignalChannel = hasPhysicalChannel ? 0 : -1
        };
    }

    private static DecodedDtcRecord Snapshot(int did, bool verified = true,
        DtcRecordKind kind = DtcRecordKind.Snapshot)
    {
        var field = new DecodedDtcRecordValue("Speed", "2A", "42", "km/h", "",
            did, 42, 80, 8, verified
                ? [new DiagnosticSourceByteSpan(3, 5, 10, 1)] : null);
        return new DecodedDtcRecord(0x123456, 0x09, kind, 1, "01 F4 05 2A",
            [field], "");
    }

    [Fact]
    public void OnlyExactDidAndSamePhysicalLoggerWithBothFrameEvidenceSetsAreLinked()
    {
        var good = Candidate(0, 0xF405);
        var unrelatedLogger = Candidate(1, 0xF405);
        var wrongDid = Candidate(0, 0xF406);
        var missingPairs = Candidate(0, 0xF405, pairs: 0);
        var unverifiedChannel = Candidate(0, 0xF405, hasPhysicalChannel: false);
        var rejected = Candidate(0, 0xF405, verdict: CorrelationVerdict.Rejected);
        var links = DtcSnapshotCanLinker.Find(0, [Snapshot(0xF405)],
            [good, unrelatedLogger, wrongDid, missingPairs, unverifiedChannel, rejected]);

        var link = Assert.Single(links);
        Assert.Same(good, link.Correlation);
        Assert.Equal(0xF405, link.Did);
        Assert.Contains("not proven", link.EvidenceSummary);
    }

    [Fact]
    public void NoOriginalSourceOrNoVerifiedSnapshotBytesProducesNoLinks()
    {
        var candidate = Candidate(0, 0xF405);
        Assert.Empty(DtcSnapshotCanLinker.Find(-1, [Snapshot(0xF405)], [candidate]));
        Assert.Empty(DtcSnapshotCanLinker.Find(0, [Snapshot(0xF405, false)], [candidate]));
        Assert.Empty(DtcSnapshotCanLinker.Find(0,
            [Snapshot(0xF405, kind: DtcRecordKind.ExtendedData)], [candidate]));
        Assert.Empty(DtcSnapshotCanLinker.Find(0, [Snapshot(0xF406)], [candidate]));
    }


    [Fact]
    public void StructuredSnapshotDidMustNotBorrowAnotherSubfieldCanCorrelation()
    {
        var template = Candidate(0, 0xF405);
        var matching = template with
        {
            Points = [new CorrelationPoint(10, 10, 10, 10, 10, 0, 3, 4,
                DidField: "DID 0xF405/Speed")]
        };
        var wrongField = template with
        {
            Points = [new CorrelationPoint(10, 10, 10, 10, 10, 0, 3, 4,
                DidField: "DID 0xF405/Pressure")]
        };
        var snapshot = Snapshot(0xF405);
        var field = snapshot.Values[0] with { Name = "DID 0xF405 · VehicleData/Speed" };
        var record = snapshot with { Values = [field] };

        var link = Assert.Single(DtcSnapshotCanLinker.Find(0,
            [record], [matching, wrongField]));
        Assert.Same(matching, link.Correlation);
        Assert.Equal("Recorded field", link.FieldScope);
        Assert.Contains("not proven", link.EvidenceSummary);
    }

    [Fact]
    public void SelectedEventAloneControlsSnapshotCanLinks()
    {
        var candidate = Candidate(0, 0xF405);
        Assert.Single(DtcSnapshotCanLinker.Find(0, [Snapshot(0xF405)], [candidate]));
        // An unrelated later clear/read event must not silently inherit
        // a previous snapshot's decoded fields and CAN links.
        Assert.Empty(DtcSnapshotCanLinker.Find(0, [], [candidate]));
    }

    [Fact]
    public void RawOnlySnapshotDidCannotBecomeNumericDbcLink()
    {
        var snapshot = Snapshot(0xF405);
        var rawOnly = snapshot with
        {
            Values = [snapshot.Values[0] with
            {
                Raw = "2A",
                Physical = "2A",
                NumericValue = null,
                Meaning = "No valid CDD/ODX conversion available"
            }]
        };
        Assert.Empty(DtcSnapshotCanLinker.Find(0, [rawOnly], [Candidate(0, 0xF405)]));
    }

    [Fact]
    public void SnapshotFieldUnitsMustAgreeWithDbcSignalUnits()
    {
        var original = Snapshot(0xF405);
        var invalid = original with
        {
            Values = [original.Values[0] with { Unit = "kPa" }]
        };
        Assert.Empty(DtcSnapshotCanLinker.Find(0, [invalid], [Candidate(0, 0xF405)]));
    }

    [Fact]
    public void CrossLoggerSnapshotFrameOrCorrelationPairCannotBecomeLink()
    {
        var candidate = Candidate(0, 0xF405);
        var snapshot = Snapshot(0xF405);
        Assert.Single(DtcSnapshotCanLinker.Find(0, [snapshot], [candidate], _ => 0));
        Assert.Empty(DtcSnapshotCanLinker.Find(0, [snapshot], [candidate],
            index => index == 3 ? 1 : 0));
        Assert.Empty(DtcSnapshotCanLinker.Find(0, [snapshot], [candidate],
            index => index == 4 ? 1 : 0));
    }

    [Fact]
    public void SourceAtOutOfRangeFrameIsRejectedWithoutCrashingDataStudio()
    {
        var candidate = Candidate(0, 0xF405);
        Assert.Empty(DtcSnapshotCanLinker.Find(0,
            [Snapshot(0xF405)], [candidate],
            index => index == 3 ? throw new ArgumentOutOfRangeException(nameof(index)) : 0));
    }

    [Fact]
    public void TransportSegmentsFromAnotherLoggerInvalidateCandidate()
    {
        var candidate = Candidate(0, 0xF405);
        var point = candidate.Points[0] with
        {
            DidResponseFrameIndices = [3, 7]
        };
        var altered = candidate with { Points = [point] };
        Assert.Empty(DtcSnapshotCanLinker.Find(0,
            [Snapshot(0xF405)], [altered], index => index == 7 ? 1 : 0));
        Assert.Single(DtcSnapshotCanLinker.Find(0,
            [Snapshot(0xF405)], [altered], _ => 0));
    }

    [Fact]
    public void TwoSnapshotSubfieldsWithSameLeafCannotBecomeAnUnambiguousFieldMatch()
    {
        var template = Candidate(0, 0xF405);
        var candidate = template with
        {
            Points = [template.Points[0] with { DidField = "DID 0xF405/Speed" }]
        };
        var snapshot = Snapshot(0xF405);
        var first = snapshot.Values[0] with { Name = "DID 0xF405 · Vehicle/Speed" };
        var second = snapshot.Values[0] with { Name = "DID 0xF405 · Axle/Speed" };
        var duplicateLeaves = snapshot with { Values = [first, second] };

        Assert.Empty(DtcSnapshotCanLinker.Find(0, [duplicateLeaves], [candidate]));
    }

    [Fact]
    public void DtcSnapshotCannotUseCorrelationFromAnotherEcuOnSameLogger()
    {
        var candidate = Candidate(0, 0xF405);
        Assert.Single(DtcSnapshotCanLinker.Find(0,
            [Snapshot(0xF405)], [candidate], observedEcu: "ECU"));
        Assert.Empty(DtcSnapshotCanLinker.Find(0,
            [Snapshot(0xF405)], [candidate], observedEcu: "BCM"));
        Assert.Empty(DtcSnapshotCanLinker.Find(0,
            [Snapshot(0xF405)], [candidate], observedEcu: ""));
    }

    [Fact]
    public void DidOnlyCandidateCannotBeBorrowedByMultipleFieldsOfOneSnapshot()
    {
        var candidate = Candidate(0, 0xF405);
        var snapshot = Snapshot(0xF405);
        var first = snapshot.Values[0] with { Name = "DID 0xF405/Speed" };
        var second = snapshot.Values[0] with { Name = "DID 0xF405/Pressure" };
        Assert.Empty(DtcSnapshotCanLinker.Find(0,
            [snapshot with { Values = [first, second] }], [candidate]));
    }

    [Fact]
    public void PartialButUnambiguousSnapshotFieldPreservesNavigationWithWarning()
    {
        var partial = Snapshot(0xF405) with
        {
            Note = "8 unmapped bits remain in this record"
        };
        var link = Assert.Single(DtcSnapshotCanLinker.Find(0,
            [partial], [Candidate(0, 0xF405)], observedEcu: "ECU"));
        Assert.False(link.SnapshotRecordFullyDecoded);
        Assert.Contains("partially decoded", link.EvidenceSummary);
        Assert.Contains("not proven", link.EvidenceSummary);

        var complete = Assert.Single(DtcSnapshotCanLinker.Find(0,
            [Snapshot(0xF405)], [Candidate(0, 0xF405)]));
        Assert.True(complete.SnapshotRecordFullyDecoded);
        Assert.DoesNotContain("partially decoded", complete.EvidenceSummary);
    }

    [Fact]
    public void RealMultiFrameDtcSnapshotCanOnlyLeadToSameEcuLoggerAndPhysicalCanSource()
    {
        var speed = new DidParameter(0xF405, "Speed", "Vehicle speed", "",
            "km/h", 0, 255, 0.5, 0, "A_UINT8", 8, "ECM", "engine.cdd");
        var catalog = new DidCatalog("Engine", "ECM", "engine.cdd",
            [speed],
            Connections:
            [
                new DiagnosticConnection("Engine", 0x7E0u, 0x7E8u,
                    false, "normal", "ECM", "engine.cdd")
            ]);
        var frames = new[]
        {
            new CanFrame(0, 0x7E0,
                [0x06, 0x19, 0x04, 0x12, 0x34, 0x56, 0x01, 0], 0),
            new CanFrame(0, 0x7E8,
                [0x10, 0x0B, 0x59, 0x04, 0x12, 0x34, 0x56, 0x09], 1_000_000),
            new CanFrame(0, 0x7E8,
                [0x21, 0x01, 0x01, 0xF4, 0x05, 0x2A, 0, 0], 2_000_000),
            new CanFrame(0, 0x100, [0x2A, 0, 0, 0, 0, 0, 0, 0], 3_000_000),
            new CanFrame(0, 0x100, [0x2B, 0, 0, 0, 0, 0, 0, 0], 4_000_000)
        };
        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(
            frames, [catalog], sourceAt: _ => 0));
        var record = Assert.Single(tx.Decoded.DtcRecords);
        var decoded = Assert.Single(record.Values);
        Assert.Equal(0xF405, decoded.Did);
        Assert.Equal(21d, decoded.NumericValue);
        Assert.True(decoded.HasVerifiedSource);
        Assert.All(decoded.SourceSpans!, span => Assert.Equal(2, span.FrameIndex));

        var hypothesis = Candidate(0, 0xF405, ecu: "ECM");
        hypothesis = hypothesis with
        {
            Points = [new CorrelationPoint(
                2_000_000, 4_000_000, 21, 21, 21, 0,
                2, 4, DidResponseFrameIndices: [1, 2])]
        };

        var link = Assert.Single(DtcSnapshotCanLinker.Find(0,
            [record], [hypothesis], sourceAt: _ => 0, observedEcu: "ECM"));
        Assert.Same(decoded, link.SnapshotField);
        Assert.Contains("not proven", link.EvidenceSummary);
        // Changing the physical recorder identity of a paired CAN frame
        // invalidates the lead, even if the CAN message and time still fit.
        Assert.Empty(DtcSnapshotCanLinker.Find(0, [record], [hypothesis],
            sourceAt: index => index == 4 ? 1 : 0, observedEcu: "ECM"));
        Assert.Empty(DtcSnapshotCanLinker.Find(0, [record], [hypothesis],
            sourceAt: _ => 0, observedEcu: "BCM"));
    }
}
