using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class UdsDidSourceProvenanceTests
{
    private static DidCatalog Catalog(params DiagnosticFieldDefinition[] fields) =>
        new("synthetic", "ECM", "local-synthetic.odx",
        [
            new DidParameter(0xF405, "Data", "Data", "", "", null, null,
                null, null, "A_UINT16", fields.Sum(field => field.BitLength),
                "ECM", "local-synthetic.odx", fields)
        ], Connections:
        [
            new DiagnosticConnection("physical", 0x7E0, 0x7E8,
                false, "normal", "ECM", "local-synthetic.odx")
        ]);

    private static DiagnosticFieldDefinition Field(
        string name, int bitOffset, int bitLength, double factor = 1) =>
        new(name, name, "", bitOffset, bitLength, $"A_UINT{bitLength}",
            "", null, null, factor, 0, false, "BIG_ENDIAN");

    [Fact]
    public void ConflictingOdxLayoutsNeverProduceVerifiedNumericFieldOrSourceBitLocation()
    {
        var field = Field("ConflictedTemperature", 0, 8);
        var catalog = new DidCatalog("synthetic", "ECM", "synthetic.odx",
        [
            new DidParameter(0xF405, "Conflicted", "Conflicted",
                "", "", null, null, 1, 0, "A_UINT8", 8,
                "ECM", "synthetic.odx", [field],
                DefinitionIssues: ["Variant A bit 0 / Variant B bit 8 disagree"])
        ], Connections:
        [
            new DiagnosticConnection("physical", 0x7E0, 0x7E8,
                false, "normal", "ECM", "synthetic.odx")
        ]);
        var frames = new[]
        {
            new CanFrame(0, 0x7E0,
                [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8,
                [0x04, 0x62, 0xF4, 0x05, 0x28, 0, 0, 0],
                1_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        var decoded = Assert.Single(transaction.Decoded.Values);
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.False(transaction.Decoded.DidPayloadFullyDecoded);
        Assert.Contains("Incomplete DID layout", transaction.Quality);
        Assert.Null(decoded.NumericValue);
        Assert.Null(decoded.ResponseBitOffset);
        Assert.False(decoded.HasVerifiedSource);
        Assert.Equal(decoded.Raw, decoded.Physical);
        Assert.Contains("Unresolved or conflicting CDD/ODX DID layout", decoded.Meaning);
    }

    [Fact]
    public void EveryDidSubparameterPointsAtItsOwnOriginalCanPayloadByte()
    {
        var catalog = Catalog(Field("Temperature", 0, 8), Field("Mode", 8, 8, 2));
        var frames = new[]
        {
            new CanFrame(0, 0x7E0,
                [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8,
                [0x05, 0x62, 0xF4, 0x05, 0x28, 0x32, 0, 0], 1_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(DiagnosticEvidence.Confirmed, transaction.Endpoint.Evidence);
        Assert.Equal(2, transaction.Decoded.Values.Count);
        Assert.Contains("2 distinct decoded fields", transaction.Decoded.ResponseSummary);

        var temperature = Assert.Single(transaction.Decoded.Values.Where(v => v.Name == "Temperature"));
        var mode = Assert.Single(transaction.Decoded.Values.Where(v => v.Name == "Mode"));
        Assert.Equal(40d, temperature.NumericValue);
        Assert.Equal(100d, mode.NumericValue);
        Assert.Equal(24, temperature.ResponseBitOffset);
        Assert.Equal(32, mode.ResponseBitOffset);
        Assert.True(temperature.HasVerifiedSource);
        Assert.True(mode.HasVerifiedSource);
        Assert.Equal(new DiagnosticSourceByteSpan(1, 4, 3, 1),
            Assert.Single(temperature.SourceSpans!));
        Assert.Equal(new DiagnosticSourceByteSpan(1, 5, 4, 1),
            Assert.Single(mode.SourceSpans!));
        Assert.Contains("CAN frame #2, byte 5", mode.SourceEvidenceText);
    }

    [Fact]
    public void MultiFrameDidSubparameterCrossesTwoRealOriginalSourceFrames()
    {
        var catalog = Catalog(Field("First32", 0, 32), Field("Second32", 32, 32));
        var frames = new[]
        {
            new CanFrame(0, 0x7E0,
                [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8,
                [0x10, 0x0B, 0x62, 0xF4, 0x05, 0x11, 0x22, 0x33], 1_000_000),
            new CanFrame(0, 0x7E8,
                [0x21, 0x44, 0x55, 0x66, 0x77, 0x88, 0, 0], 2_000_000)
        };
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        var first = Assert.Single(transaction.Decoded.Values.Where(v => v.Name == "First32"));
        var second = Assert.Single(transaction.Decoded.Values.Where(v => v.Name == "Second32"));

        Assert.Equal((double)0x11223344, first.NumericValue);
        Assert.Equal((double)0x55667788, second.NumericValue);
        Assert.Equal(new[]
        {
            new DiagnosticSourceByteSpan(1, 5, 3, 3),
            new DiagnosticSourceByteSpan(2, 1, 6, 1)
        }, first.SourceSpans);
        Assert.Equal(new DiagnosticSourceByteSpan(2, 2, 7, 4),
            Assert.Single(second.SourceSpans!));
    }

    [Fact]
    public void IncompleteTransportNeverProducesVerifiedNumericDidValues()
    {
        var catalog = Catalog(Field("Temperature", 0, 8));
        var frames = new[]
        {
            new CanFrame(0, 0x7E0,
                [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            // Declares eight UDS bytes but only six of them were captured.
            new CanFrame(0, 0x7E8,
                [0x10, 0x08, 0x62, 0xF4, 0x05, 0x33, 0x44, 0x55], 1_000_000)
        };
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        Assert.Equal(DiagnosticOutcome.Malformed, transaction.Outcome);
        Assert.All(transaction.Decoded.Values, value =>
        {
            Assert.False(value.HasVerifiedSource);
            Assert.Null(value.NumericValue);
            Assert.Contains("not verified", value.Meaning);
        });
    }

    [Fact]
    public void MultiDidResponseMapsSecondDidToItsActualLaterCanByte()
    {
        var first = new DidParameter(0xF405, "First", "First", "", "", null, null,
            null, null, "A_UINT8", 8, "ECM", "synthetic.odx",
            [Field("FirstField", 0, 8)]);
        var second = new DidParameter(0xF406, "Second", "Second", "", "", null, null,
            null, null, "A_UINT8", 8, "ECM", "synthetic.odx",
            [Field("SecondField", 0, 8)]);
        var catalog = new DidCatalog("synthetic", "ECM", "synthetic.odx",
            [first, second], Connections:
            [
                new DiagnosticConnection("physical", 0x7E0, 0x7E8,
                    false, "normal", "ECM", "synthetic.odx")
            ]);
        var frames = new[]
        {
            new CanFrame(0, 0x7E0,
                [0x05, 0x22, 0xF4, 0x05, 0xF4, 0x06, 0, 0], 0),
            new CanFrame(0, 0x7E8,
                [0x07, 0x62, 0xF4, 0x05, 0x20, 0xF4, 0x06, 0x30], 1_000_000)
        };
        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        Assert.Equal(2, tx.Decoded.Values.Count);
        var firstValue = Assert.Single(tx.Decoded.Values.Where(value => value.Did == 0xF405));
        var secondValue = Assert.Single(tx.Decoded.Values.Where(value => value.Did == 0xF406));
        Assert.Equal(24, firstValue.ResponseBitOffset);
        Assert.Equal(48, secondValue.ResponseBitOffset);
        Assert.Equal(new DiagnosticSourceByteSpan(1, 4, 3, 1),
            Assert.Single(firstValue.SourceSpans!));
        Assert.Equal(new DiagnosticSourceByteSpan(1, 7, 6, 1),
            Assert.Single(secondValue.SourceSpans!));
    }

    [Fact]
    public void PackedBitSubparameterKeepsItsPreciseResponseBitInterval()
    {
        var catalog = Catalog(Field("FlagGroup", 1, 3));
        var frames = new[]
        {
            new CanFrame(0, 0x7E0, [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8, [0x04, 0x62, 0xF4, 0x05, 0xA0, 0, 0, 0],
                1_000_000)
        };
        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        var value = Assert.Single(tx.Decoded.Values);
        Assert.Equal(2d, value.NumericValue);
        Assert.Equal(25, value.ResponseBitOffset);
        Assert.Equal(3, value.ResponseBitLength);
        Assert.Equal(new DiagnosticSourceByteSpan(1, 4, 3, 1),
            Assert.Single(value.SourceSpans!));
    }

    [Fact]
    public void FloatEncodedDidKeepsVerifiedOriginalBytesButNeverAssumesIntegerScaling()
    {
        var field = new DiagnosticFieldDefinition(
            "IeeeFloat", "IeeeFloat", "", 0, 32, "A_FLOAT32", "",
            null, null, 1, 0, false, "BIG_ENDIAN");
        var catalog = Catalog(field);
        var frames = new[]
        {
            new CanFrame(0, 0x7E0,
                [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8,
                [0x07, 0x62, 0xF4, 0x05, 0x3F, 0x80, 0x00, 0x00],
                1_000_000)
        };
        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        var value = Assert.Single(tx.Decoded.Values);
        Assert.Equal(DiagnosticOutcome.Positive, tx.Outcome);
        Assert.True(value.HasVerifiedSource);
        Assert.Null(value.NumericValue);
        Assert.Equal("3F 80 00 00", value.Raw);
        Assert.Contains("not a verified integer", value.Meaning);
        Assert.Equal(new DiagnosticSourceByteSpan(1, 4, 3, 4),
            Assert.Single(value.SourceSpans!));
    }

    [Fact]
    public void KnownAsciiDidFieldRemainsHumanReadableAndSourceLinked()
    {
        var field = new DiagnosticFieldDefinition(
            "Label", "Label", "", 0, 24, "A_ASCIISTRING", "",
            null, null, null, null, false, "BIG_ENDIAN");
        var catalog = Catalog(field);
        var frames = new[]
        {
            new CanFrame(0, 0x7E0, [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8, [0x06, 0x62, 0xF4, 0x05, 0x41, 0x42, 0x43, 0],
                1_000_000)
        };
        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        var value = Assert.Single(tx.Decoded.Values);
        Assert.Equal("ABC", value.Physical);
        Assert.Null(value.NumericValue);
        Assert.True(value.HasVerifiedSource);
        Assert.Equal(new DiagnosticSourceByteSpan(1, 4, 3, 3),
            Assert.Single(value.SourceSpans!));
    }

    [Fact]
    public void WideRawIntegerNeverClaimsExactPhysicalDoubleEvenWithVerifiedCanBytes()
    {
        var catalog = Catalog(Field("Wide", 0, 64));
        var frames = new[]
        {
            new CanFrame(0, 0x7E0,
                [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8,
                [0x10, 0x0B, 0x62, 0xF4, 0x05, 0xFF, 0xFF, 0xFF], 1_000_000),
            new CanFrame(0, 0x7E8,
                [0x21, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0], 2_000_000)
        };
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        var value = Assert.Single(transaction.Decoded.Values);
        Assert.True(value.HasVerifiedSource);
        Assert.Null(value.NumericValue);
        Assert.Equal("0xFFFFFFFFFFFFFFFF", value.Raw);
        Assert.Contains("not exactly representable", value.Meaning);
    }
}
