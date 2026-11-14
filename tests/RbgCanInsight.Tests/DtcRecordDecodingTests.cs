using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DtcRecordDecodingTests
{
    private static IsoTpMessage Message(uint id, byte[] payload, long time = 0) =>
        new(0, id, false, time, time, [0], payload, true, string.Empty);

    private static DecodedUdsOperation Decode(byte[] request, byte[] response, params DidCatalog[] catalogs) =>
        UdsServiceDecoder.Decode(Message(0x7E0, request), [Message(0x7E8, response, 1_000_000)], catalogs);

    private static DidParameter SpeedDid() => new(
        0xF405, "Speed", "Vehicle speed", "", "km/h", 0, 255, 0.5, 0, "A_UINT8", 8, "ECM", "fixture.cdd");

    private static DiagnosticFieldDefinition Field(string name, int offset, int length) => new(
        name, name, "", offset, length, "A_UINT8", "", null, null, 1, 0, false, "BIG_ENDIAN");

    private static DidCatalog Catalog(DidParameter[]? dids = null, DiagnosticTroubleCode[]? dtcs = null) =>
        new("fixture", "ECM", "fixture.cdd", dids ?? [], dtcs);

    [Fact]
    public void SnapshotRecordIsDecodedWithTheCatalogDidDefinition()
    {
        var response = new byte[] { 0x59, 0x04, 0x12, 0x34, 0x56, 0x09, 0x01, 0x01, 0xF4, 0x05, 0x2A };
        var decoded = Decode([0x19, 0x04, 0x12, 0x34, 0x56, 0xFF], response, Catalog([SpeedDid()]));

        var record = Assert.Single(decoded.DtcRecords);
        Assert.Equal(DtcRecordKind.Snapshot, record.Kind);
        Assert.Equal(0x123456, record.Code);
        Assert.Equal(1, record.RecordNumber);
        Assert.Equal((byte)0x09, record.Status);
        var value = Assert.Single(record.Values);
        Assert.Equal(0xF405, value.Did);
        Assert.Equal("21", value.Physical);
        Assert.Equal(21d, value.NumericValue);
        Assert.True(record.IsFullyDecoded);
        Assert.Contains("Vehicle speed = 21 km/h", decoded.Dtcs[0].ExtraData);
    }

    [Fact]
    public void TwoSnapshotRecordsAreSplitWhenEveryDidWidthIsDeclared()
    {
        var response = new byte[]
        {
            0x59, 0x04, 0x12, 0x34, 0x56, 0x09,
            0x01, 0x01, 0xF4, 0x05, 0x10,
            0x02, 0x01, 0xF4, 0x05, 0x20
        };
        var decoded = Decode([0x19, 0x04, 0x12, 0x34, 0x56, 0xFF], response, Catalog([SpeedDid()]));
        Assert.Equal(2, decoded.DtcRecords.Count);
        Assert.Equal("8", decoded.DtcRecords[0].Values[0].Physical);
        Assert.Equal("16", decoded.DtcRecords[1].Values[0].Physical);
        Assert.Equal(new[] { 1, 2 }, decoded.DtcRecords.Select(r => r.RecordNumber));
    }

    [Fact]
    public void SnapshotWithoutDeclaredDidWidthStaysRawAndIsFlagged()
    {
        var response = new byte[] { 0x59, 0x04, 0x12, 0x34, 0x56, 0x09, 0x01, 0x01, 0xF4, 0x05, 0x2A, 0x02, 0x00 };
        var decoded = Decode([0x19, 0x04, 0x12, 0x34, 0x56, 0xFF], response, Catalog());

        var record = Assert.Single(decoded.DtcRecords);
        Assert.False(record.IsFullyDecoded);
        Assert.Contains("no declared width", record.Note);
        Assert.Contains("2A 02 00", record.Values[0].Raw);
        Assert.Null(record.Values[0].NumericValue);
    }

    [Fact]
    public void ExtendedDataIsDecodedFromDtcLevelDefinitionsForASingleRequestedRecord()
    {
        var dtc = new DiagnosticTroubleCode(0x123456, "P0123", "Sensor low", "", "ECM", "fixture.cdd",
            null, [Field("OccurrenceCounter", 0, 8), Field("AgingCounter", 8, 8)]);
        var response = new byte[] { 0x59, 0x06, 0x12, 0x34, 0x56, 0x2F, 0x01, 0x05, 0x0A };
        var decoded = Decode([0x19, 0x06, 0x12, 0x34, 0x56, 0x01], response, Catalog(dtcs: [dtc]));

        var record = Assert.Single(decoded.DtcRecords);
        Assert.Equal(DtcRecordKind.ExtendedData, record.Kind);
        Assert.Equal(new[] { "5", "10" }, record.Values.Select(v => v.Physical));
        Assert.Equal("OccurrenceCounter", record.Values[0].Name);
        Assert.True(record.IsFullyDecoded);
        Assert.Equal("Sensor low", decoded.Dtcs[0].Name);
    }



    [Fact]
    public void OverlappingLoggerResponsesNeverShareDtcFieldCanByteEvidence()
    {
        var dtc = new DiagnosticTroubleCode(0x123456, "P0123", "Counter", "", "ECM",
            "fixture.cdd", null, [Field("Count", 0, 16)]);
        var catalog = Catalog(dtcs: [dtc]);
        var frames = new[]
        {
            new RbgCanInsight.Domain.CanFrame(0, 0x7E0,
                [0x06, 0x19, 0x06, 0x12, 0x34, 0x56, 0x01, 0], 0),
            new RbgCanInsight.Domain.CanFrame(0, 0x7E0,
                [0x06, 0x19, 0x06, 0x12, 0x34, 0x56, 0x01, 0], 0),
            new RbgCanInsight.Domain.CanFrame(0, 0x7E8,
                [0x10, 0x09, 0x59, 0x06, 0x12, 0x34, 0x56, 0x2F], 1_000_000),
            new RbgCanInsight.Domain.CanFrame(0, 0x7E8,
                [0x10, 0x09, 0x59, 0x06, 0x12, 0x34, 0x56, 0x2F], 1_000_000),
            new RbgCanInsight.Domain.CanFrame(0, 0x7E8,
                [0x21, 0x01, 0x05, 0x0A, 0, 0, 0, 0], 2_000_000),
            new RbgCanInsight.Domain.CanFrame(0, 0x7E8,
                [0x21, 0x01, 0x06, 0x0B, 0, 0, 0, 0], 2_000_000)
        };
        int SourceAt(int index) => index % 2;
        var transactions = UdsTransactionAnalyzer.Analyze(frames, [catalog],
            sourceAt: SourceAt);
        var decoded = transactions.Where(transaction =>
            transaction.Decoded.ServiceId == 0x19 &&
            transaction.Decoded.DtcRecords.Count > 0).ToArray();
        Assert.Equal(2, decoded.Length);
        var seen = new HashSet<int>();
        foreach (var transaction in decoded)
        {
            var record = Assert.Single(transaction.Decoded.DtcRecords);
            var field = Assert.Single(record.Values);
            Assert.True(field.HasVerifiedSource);
            var source = SourceAt(transaction.Responses[0].FrameIndices[0]);
            Assert.True(seen.Add(source));
            Assert.All(record.SourceSpans!, span =>
                Assert.Equal(source, SourceAt(span.FrameIndex)));
            Assert.All(field.SourceSpans!, span =>
                Assert.Equal(source, SourceAt(span.FrameIndex)));
            Assert.Equal(source == 0 ? 5d * 256 + 10 : 6d * 256 + 11,
                field.NumericValue);
        }
    }


    [Fact]
    public void SnapshotDidWithUndeclaredBitsIsPartialEvenWhenKnownFieldsDecode()
    {
        var did = new DidParameter(0xF405, "Speed", "Vehicle speed", "", "km/h",
            0, 255, 1, 0, "A_UINT16", 16, "ECM", "fixture.cdd",
            FieldDefinitions: [Field("OnlySecondByte", 8, 8)]);
        var response = new byte[]
        {
            0x59, 0x04, 0x12, 0x34, 0x56, 0x09,
            0x01, 0x01, 0xF4, 0x05, 0xAA, 0x2A
        };
        var decoded = Decode([0x19, 0x04, 0x12, 0x34, 0x56, 0x01],
            response, Catalog([did]));
        var record = Assert.Single(decoded.DtcRecords);
        Assert.Single(record.Values);
        Assert.False(record.IsFullyDecoded);
        Assert.Contains("8 bit(s) not covered", record.Note);
        Assert.Contains("AA 2A", record.RawHex);
        Assert.Equal(88, record.Values[0].ResponseBitOffset);
    }

    [Fact]
    public void SnapshotDidWithFullyCoveredFieldLayoutIsFullyDecoded()
    {
        var did = new DidParameter(0xF405, "Speed", "Vehicle speed", "", "km/h",
            0, 255, 1, 0, "A_UINT16", 16, "ECM", "fixture.cdd",
            FieldDefinitions: [Field("High", 0, 8), Field("Low", 8, 8)]);
        var response = new byte[]
        {
            0x59, 0x04, 0x12, 0x34, 0x56, 0x09,
            0x01, 0x01, 0xF4, 0x05, 0xAA, 0x2A
        };
        var record = Assert.Single(Decode(
            [0x19, 0x04, 0x12, 0x34, 0x56, 0x01], response,
            Catalog([did])).DtcRecords);
        Assert.Equal(2, record.Values.Count);
        Assert.True(record.IsFullyDecoded);
        Assert.Empty(record.Note);
    }


    [Fact]
    public void OverlappingSnapshotDidFieldDefinitionsNeverClaimCompleteDecoding()
    {
        var did = new DidParameter(0xF405, "Speed", "Vehicle speed", "", "km/h",
            0, 255, 1, 0, "A_UINT16", 16, "ECM", "fixture.cdd",
            FieldDefinitions: [Field("Full", 0, 16), Field("OverlappingLow", 8, 8)]);
        var response = new byte[]
        {
            0x59, 0x04, 0x12, 0x34, 0x56, 0x09,
            0x01, 0x01, 0xF4, 0x05, 0xAA, 0x2A
        };
        var record = Assert.Single(Decode(
            [0x19, 0x04, 0x12, 0x34, 0x56, 0x01],
            response, Catalog([did])).DtcRecords);
        Assert.False(record.IsFullyDecoded);
        Assert.Contains("overlapping", record.Note);
        Assert.Equal(2, record.Values.Count);
        Assert.All(record.Values, value =>
        {
            Assert.Null(value.NumericValue);
            Assert.Equal(value.Raw, value.Physical);
            Assert.Contains("Overlapping", value.Meaning);
        });
        Assert.Contains("AA 2A", record.RawHex);
    }

    [Fact]
    public void OverlappingExtendedDataFieldsRemainExplicitlyAmbiguous()
    {
        var dtc = new DiagnosticTroubleCode(0x123456, "P0123", "Counter", "", "ECM",
            "fixture.cdd", null, [Field("Whole", 0, 16), Field("Low", 8, 8)]);
        var response = new byte[]
        {
            0x59, 0x06, 0x12, 0x34, 0x56, 0x2F, 0x01, 0x05, 0x0A
        };
        var record = Assert.Single(Decode(
            [0x19, 0x06, 0x12, 0x34, 0x56, 0x01],
            response, Catalog(dtcs: [dtc])).DtcRecords);
        Assert.False(record.IsFullyDecoded);
        Assert.Contains("overlapping", record.Note);
        Assert.Equal(2, record.Values.Count);
        Assert.All(record.Values, value =>
        {
            Assert.Null(value.NumericValue);
            Assert.Equal(value.Raw, value.Physical);
            Assert.Contains("Overlapping", value.Meaning);
        });
        Assert.Equal("01 05 0A", record.RawHex);
    }

    [Fact]
    public void SparseDtcFieldLayoutNeverClaimsFullRecordCoverage()
    {
        var dtc = new DiagnosticTroubleCode(0x123456, "P0123", "Partial", "", "ECM",
            "fixture.cdd", null, [Field("OnlyLastByte", 8, 8)]);
        var response = new byte[] { 0x59, 0x06, 0x12, 0x34, 0x56, 0x08, 0x01, 0xAB, 0x22 };
        var result = Decode([0x19, 0x06, 0x12, 0x34, 0x56, 0x01],
            response, Catalog(dtcs: [dtc]));
        var record = Assert.Single(result.DtcRecords);
        var value = Assert.Single(record.Values);
        Assert.Equal("OnlyLastByte", value.Name);
        Assert.Equal(64, value.ResponseBitOffset);
        Assert.False(record.IsFullyDecoded);
        Assert.Contains("8 bit(s) are not covered", record.Note);
        Assert.Equal("01 AB 22", record.RawHex);
    }

    [Fact]
    public void ExtendedDataWithoutDefinitionKeepsRawBytesAndSaysWhy()
    {
        var response = new byte[] { 0x59, 0x06, 0x12, 0x34, 0x56, 0x2F, 0x01, 0x05, 0x0A };
        var decoded = Decode([0x19, 0x06, 0x12, 0x34, 0x56, 0x01], response, Catalog());
        var record = Assert.Single(decoded.DtcRecords);
        Assert.Empty(record.Values);
        Assert.Contains("no extended-data definition", record.Note);
        Assert.Equal("01 05 0A", record.RawHex);
    }

    [Fact]
    public void ExtendedDataForAllRecordsIsNotSplitWithoutALengthField()
    {
        var response = new byte[] { 0x59, 0x06, 0x12, 0x34, 0x56, 0x2F, 0x01, 0x05, 0x0A, 0x02, 0x07 };
        var decoded = Decode([0x19, 0x06, 0x12, 0x34, 0x56, 0xFF], response, Catalog());
        var record = Assert.Single(decoded.DtcRecords);
        Assert.Contains("boundaries unresolved", record.Note);
        Assert.Equal("01 05 0A 02 07", record.RawHex);
    }

    [Fact]
    public void StoredDataByRecordNumberYieldsOneRecordPerDtc()
    {
        var response = new byte[]
        {
            0x59, 0x05,
            0x01, 0xAA, 0xBB, 0xCC, 0x08, 0x01, 0xF4, 0x05, 0x10,
            0x01, 0x11, 0x22, 0x33, 0x09, 0x01, 0xF4, 0x05, 0x20
        };
        var decoded = Decode([0x19, 0x05, 0x01], response, Catalog([SpeedDid()]));
        Assert.Equal(2, decoded.DtcRecords.Count);
        Assert.Equal(new[] { 0xAABBCC, 0x112233 }, decoded.Dtcs.Select(d => d.Code));
        Assert.Equal((byte)0x09, decoded.Dtcs[1].Status);
        Assert.Equal("16", decoded.DtcRecords[1].Values[0].Physical);
    }


    [Fact]
    public void DtcSnapshotFieldMapsToExactOriginalCanPayloadByte()
    {
        var catalog = Catalog([SpeedDid()]);
        var frames = new[]
        {
            new RbgCanInsight.Domain.CanFrame(0, 0x7E0,
                [0x06, 0x19, 0x04, 0x12, 0x34, 0x56, 0x01, 0x00], 0),
            new RbgCanInsight.Domain.CanFrame(0, 0x7E8,
                [0x10, 0x0B, 0x59, 0x04, 0x12, 0x34, 0x56, 0x09], 1_000_000),
            new RbgCanInsight.Domain.CanFrame(0, 0x7E8,
                [0x21, 0x01, 0x01, 0xF4, 0x05, 0x2A, 0, 0], 2_000_000)
        };
        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        Assert.Equal(DiagnosticOutcome.Positive, tx.Outcome);
        var record = Assert.Single(tx.Decoded.DtcRecords);
        var field = Assert.Single(record.Values);
        Assert.Equal(80, field.ResponseBitOffset);
        Assert.Equal(8, field.ResponseBitLength);
        Assert.Equal(2, Assert.Single(field.SourceSpans!).FrameIndex);
        Assert.Equal(5, Assert.Single(field.SourceSpans!).CanPayloadByteOffset);
        Assert.Equal(21d, field.NumericValue);
        Assert.True(field.HasVerifiedSource);
        Assert.True(record.HasVerifiedSource);
        Assert.Contains("frame #3", field.SourceEvidenceText);
    }

    [Fact]
    public void ExtendedDtcFieldsRetainBitPreciseSourceSpansAcrossIsoTpFrames()
    {
        var dtc = new DiagnosticTroubleCode(0x123456, "P0123", "Sensor low", "", "ECM",
            "fixture.cdd", null, [Field("Counter", 0, 16)]);
        var catalog = Catalog(dtcs: [dtc]);
        var frames = new[]
        {
            new RbgCanInsight.Domain.CanFrame(0, 0x7E0,
                [0x06, 0x19, 0x06, 0x12, 0x34, 0x56, 0x01, 0], 0),
            new RbgCanInsight.Domain.CanFrame(0, 0x7E8,
                [0x10, 0x09, 0x59, 0x06, 0x12, 0x34, 0x56, 0x2F], 1_000_000),
            new RbgCanInsight.Domain.CanFrame(0, 0x7E8,
                [0x21, 0x01, 0x05, 0x0A, 0, 0, 0, 0], 2_000_000)
        };
        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        var record = Assert.Single(tx.Decoded.DtcRecords);
        var field = Assert.Single(record.Values);
        Assert.True(field.HasVerifiedSource);
        Assert.Equal(56, field.ResponseBitOffset);
        Assert.Equal(16, field.ResponseBitLength);
        Assert.Equal(new DiagnosticSourceByteSpan(2, 2, 7, 2),
            Assert.Single(field.SourceSpans!));
        Assert.Equal("01 05 0A", record.RawHex);
        Assert.True(record.IsFullyDecoded);
    }

    [Fact]
    public void FaultMemoryTableCarriesStatusBitsAndLatestRecords()
    {
        var response = new byte[] { 0x59, 0x04, 0x12, 0x34, 0x56, 0x09, 0x01, 0x01, 0xF4, 0x05, 0x2A };
        var decoded = Decode([0x19, 0x04, 0x12, 0x34, 0x56, 0xFF], response, Catalog([SpeedDid()]));
        var transaction = new DiagnosticTransaction(
            1, Message(0x7E0, [0x19, 0x04, 0x12, 0x34, 0x56, 0xFF]), [Message(0x7E8, response, 1_000_000)],
            new DiagnosticEndpoint("Tester", "ECM", DiagnosticDirection.EcuToTester, DiagnosticEvidence.Confirmed, "physical"),
            DiagnosticOutcome.Positive, decoded, 0, 1_000_000, 1, [3, 4], "Recorded");

        var faults = DiagnosticInsightAnalyzer.Analyze([transaction], [Catalog([SpeedDid()])]).Faults;
        var fault = Assert.Single(faults);
        Assert.Single(fault.LatestRecords);

        var rows = FaultMemoryTable.Build(faults);
        var row = Assert.Single(rows);
        Assert.Equal("1", row.Bit(0));
        Assert.Equal("0", row.Bit(1));
        Assert.Equal("1", row.Bit(3));
        Assert.Contains("Vehicle speed = 21 km/h", row.SnapshotText);
        var csv = FaultMemoryTable.ToCsv(rows);
        Assert.StartsWith("ECU,DTC,Name,Status,Status text,testFailed,", csv);
        Assert.Contains("ECM,0x123456", csv);
        Assert.Contains("3 4", csv);
    }
}
