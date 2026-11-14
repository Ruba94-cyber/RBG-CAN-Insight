using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class MultiDidUdsDecodingTests
{
    private static DidParameter Did(int id, string ecu = "ECM", double factor = 1) =>
        new(id, $"Did{id:X4}", $"Parameter {id:X4}", "Synthetic test DID", "rpm",
            0, 65535, factor, 0, "A_UINT8", 8, ecu, "fixture.cdd");

    private static DidCatalog Catalog(string ecu, params DidParameter[] definitions) =>
        new("Synthetic", ecu, "fixture.cdd", definitions,
            Connections: [new DiagnosticConnection("ECU transport", 0x7E0u, 0x7E8u,
                false, "normal", ecu, "fixture.cdd")]);

    private static CanFrame Frame(uint id, byte[] bytes, long timestamp = 0) =>
        new(0, id, bytes, timestamp);

    private static DiagnosticTransaction Analyze(IReadOnlyList<DidCatalog> catalogs, byte[] response) =>
        Assert.Single(UdsTransactionAnalyzer.Analyze(
            [
                Frame(0x7E0, [0x05, 0x22, 0xF4, 0x05, 0xF4, 0x06, 0, 0]),
                Frame(0x7E8, response, 10_000_000)
            ], catalogs));

    [Fact]
    public void TwoKnownDidsAreDecodedIndividuallyWithoutConsumingNextDidBytes()
    {
        var transaction = Analyze([Catalog("ECM", Did(0xF405), Did(0xF406))],
            [0x07, 0x62, 0xF4, 0x05, 0x20, 0xF4, 0x06, 0x30]);

        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(2, transaction.Decoded.Values.Count);
        Assert.True(transaction.Decoded.DidPayloadFullyDecoded);
        Assert.Equal(new[] { 0xF405, 0xF406 },
            transaction.Decoded.Values.Select(value => value.Did!.Value));
        Assert.Equal(new[] { 32d, 48d },
            transaction.Decoded.Values.Select(value => value.NumericValue!.Value));
    }

    [Fact]
    public void MalformedRequestedDidByteListCannotClaimCompleteResponseLayout()
    {
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
            [0x22, 0xF4, 0x05, 0xF4], true, "");
        var response = new IsoTpMessage(0, 0x7E8, false, 1, 1, [1],
            [0x62, 0xF4, 0x05, 0x20], true, "");

        var decoded = UdsServiceDecoder.Decode(request, [response],
            [Catalog("ECM", Did(0xF405))], "ECM");
        Assert.False(decoded.DidPayloadFullyDecoded);
        Assert.Contains("Request DID list was malformed", decoded.ResponseSummary);
        Assert.Equal(32d, Assert.Single(decoded.Values).NumericValue);
    }

    [Fact]
    public void UnrequestedDuplicatePositiveDidDoesNotCountAsCompleteLayout()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0]),
            Frame(0x7E8, [0x07, 0x62, 0xF4, 0x05, 0x20, 0xF4, 0x05, 0x30],
                1_000_000)
        };
        var transaction = Assert.Single(
            UdsTransactionAnalyzer.Analyze(frames, [Catalog("ECM", Did(0xF405))]));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.False(transaction.Decoded.DidPayloadFullyDecoded);
        Assert.Contains("Unexpected repeated DID", transaction.Decoded.ResponseSummary);
        var validFirst = Assert.Single(transaction.Decoded.Values);
        Assert.Equal(32d, validFirst.NumericValue);
        Assert.True(validFirst.HasVerifiedSource);
        Assert.Contains("Incomplete DID layout", transaction.Quality);
    }

    [Fact]
    public void RepeatedDidRequestedTwiceCanBeReportedTwiceWithoutFakeAmbiguity()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x05, 0x22, 0xF4, 0x05, 0xF4, 0x05, 0, 0]),
            Frame(0x7E8, [0x07, 0x62, 0xF4, 0x05, 0x20, 0xF4, 0x05, 0x30],
                1_000_000)
        };
        var transaction = Assert.Single(
            UdsTransactionAnalyzer.Analyze(frames, [Catalog("ECM", Did(0xF405))]));
        Assert.True(transaction.Decoded.DidPayloadFullyDecoded);
        Assert.Equal(new[] { 32d, 48d },
            transaction.Decoded.Values.Select(value => value.NumericValue!.Value));
        Assert.Equal(2, transaction.Decoded.Values.Count);
        Assert.All(transaction.Decoded.Values, value => Assert.True(value.HasVerifiedSource));
    }

    [Fact]
    public void UnknownSecondDidHasNoFabricatedBoundaryOrNumericValue()
    {
        var transaction = Analyze([Catalog("ECM", Did(0xF405))],
            [0x07, 0x62, 0xF4, 0x05, 0x20, 0xF4, 0x06, 0x30]);

        var decoded = Assert.Single(transaction.Decoded.Values);
        Assert.Equal(0xF405, decoded.Did);
        Assert.Equal(32d, decoded.NumericValue);
        Assert.Contains("Cannot segment DID 0xF406", transaction.Decoded.ResponseSummary);
        Assert.False(transaction.Decoded.DidPayloadFullyDecoded);
    }

    [Fact]
    public void SameDidDefinedByTwoEcusUsesOnlyConfirmedEndpointDefinition()
    {
        // Never use whichever duplicate DID happens to appear first in CDD/ODX loading order.
        var bcm = Catalog("BCM", Did(0xF405, "BCM", factor: 10));
        var ecm = Catalog("ECM", Did(0xF405, "ECM", factor: 2));
        var response = new IsoTpMessage(0, 0x7E8, false, 0, 0, [0],
            [0x62, 0xF4, 0x05, 0x20], true, string.Empty);
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
            [0x22, 0xF4, 0x05], true, string.Empty);

        // Pass explicit ECU identity, as UdsTransactionAnalyzer now does.
        var decoded = UdsServiceDecoder.Decode(request, [response], [bcm, ecm], ecu: "ECM");
        Assert.Equal(64d, Assert.Single(decoded.Values).NumericValue);
    }

    [Fact]
    public void UnresolvedDuplicateDidNeverProducesNumericCorrelationEvidence()
    {
        var bcm = Catalog("BCM", Did(0xF405, "BCM", factor: 10));
        var ecm = Catalog("ECM", Did(0xF405, "ECM", factor: 2));
        var response = new IsoTpMessage(0, 0x7E8, false, 0, 0, [0],
            [0x62, 0xF4, 0x05, 0x20], true, string.Empty);
        var decoded = UdsServiceDecoder.Decode(null, [response], [bcm, ecm]);

        Assert.Null(Assert.Single(decoded.Values).NumericValue);
    }

    private static DecodedDiagnosticValue DecodeField(
        string byteOrder, byte[] payload, bool signed = false, double factor = 1,
        bool conversionVerified = true)
    {
        var field = new DiagnosticFieldDefinition(
            "Number", "Number", "", 0, payload.Length * 8,
            signed ? "A_INT16" : "A_UINT16", "", null, null,
            factor, 0, signed, byteOrder, SupportsPhysicalConversion: conversionVerified);
        var did = new DidParameter(0xF405, "ByteOrder", "ByteOrder", "",
            "", null, null, null, null, field.DataType, field.BitLength,
            "ECM", "test.odx", [field]);
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
            [0x22, 0xF4, 0x05], true, string.Empty);
        var response = new IsoTpMessage(0, 0x7E8, false, 0, 0, [0],
            [0x62, 0xF4, 0x05, .. payload], true, string.Empty);
        return Assert.Single(UdsServiceDecoder.Decode(request, [response], [Catalog("ECM", did)]).Values);
    }

    [Fact]
    public void PrecisePhysicalScaleIsRoundTripSafeForIndependentDecoderParity()
    {
        var value = DecodeField("BIG_ENDIAN", [0x00, 0x01],
            factor: 0.123456789012);
        Assert.NotNull(value.NumericValue);
        Assert.Equal("0.123456789012", value.Physical);
        Assert.Equal(
            value.NumericValue.Value.ToString("R",
                System.Globalization.CultureInfo.InvariantCulture),
            value.Physical);
    }

    [Fact]
    public void PlainDidScalarUsesRoundTripFormattingAsWell()
    {
        var catalog = Catalog("ECM",
            new DidParameter(0xF405, "Precise", "Precise", "", "",
                null, null, 0.123456789012, 0, "A_UINT8", 8,
                "ECM", "synthetic.odx"));
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
            [0x22, 0xF4, 0x05], true, "");
        var response = new IsoTpMessage(0, 0x7E8, false, 0, 0, [1],
            [0x62, 0xF4, 0x05, 0x01], true, "");
        var decoded = Assert.Single(
            UdsServiceDecoder.Decode(request, [response], [catalog], "ECM").Values);
        Assert.Equal("0.123456789012", decoded.Physical);
        Assert.Equal(0.123456789012d, decoded.NumericValue);
    }

    [Fact]
    public void MultiByteOdxDidUsesExplicitLittleEndianInsteadOfBigEndian()
    {
        var little = DecodeField("LITTLE_ENDIAN", [0x34, 0x12]);
        var big = DecodeField("BIG_ENDIAN", [0x34, 0x12]);
        Assert.Equal((double)0x1234, little.NumericValue);
        Assert.Equal((double)0x3412, big.NumericValue);
        Assert.Equal("0x1234", little.Raw);
        Assert.Equal("0x3412", big.Raw);
    }

    [Fact]
    public void SignedLittleEndianDiagnosticFieldsSignExtendBeforeScaling()
    {
        var value = DecodeField("LITTLE_ENDIAN", [0xFF, 0xFE], signed: true, factor: 0.5);
        Assert.Equal(-128.5d, value.NumericValue);
    }

    [Fact]
    public void UnsupportedOdxPiecewiseConversionPreservesCodedValueButNoPhysicalEvidence()
    {
        var result = DecodeField("BIG_ENDIAN", [0x12, 0x34], conversionVerified: false);
        Assert.Equal("0x1234", result.Raw);
        Assert.Equal("0x1234", result.Physical);
        Assert.Null(result.NumericValue);
        Assert.Contains("Physical transformation unsupported", result.Meaning);
    }

    [Fact]
    public void UndocumentedCddFieldByteOrderIsRawOnlyNotFalseCanCorrelationEvidence()
    {
        var value = DecodeField("Not declared", [0x34, 0x12]);
        Assert.Null(value.NumericValue);
        Assert.Contains("34 12", value.Raw);
        Assert.Contains("Source does not define", value.Meaning);
    }

    [Fact]
    public void NumericDidContainingPrintableBytesIsNotMistakenForAscii()
    {
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
            [0x22, 0xF4, 0x05], true, string.Empty);
        var response = new IsoTpMessage(0, 0x7E8, false, 0, 0, [0],
            [0x62, 0xF4, 0x05, 0x31, 0x32, 0x33], true, string.Empty);
        var did = new DidParameter(0xF405, "ThreeByte", "ThreeByte", "",
            "", null, null, 1, 0, "A_UINT24", 24, "ECM", "fixture.cdd");
        var decoded = UdsServiceDecoder.Decode(request, [response], [Catalog("ECM", did)]);
        Assert.Equal((double)0x313233, Assert.Single(decoded.Values).NumericValue);
    }

    private static DecodedUdsOperation DecodeStructuredDid(
        IReadOnlyList<DiagnosticFieldDefinition> fields, byte[] payload)
    {
        var did = new DidParameter(0xF405, "Structured", "Structured", "",
            "", null, null, 1, 0, "A_UINT8", payload.Length * 8,
            "ECM", "fixture.odx", fields);
        var catalog = Catalog("ECM", did);
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
            [0x22, 0xF4, 0x05], true, "");
        var response = new IsoTpMessage(0, 0x7E8, false, 1, 1, [1],
            [0x62, 0xF4, 0x05, .. payload], true, "");
        return UdsServiceDecoder.Decode(request, [response], [catalog], "ECM");
    }

    private static DiagnosticFieldDefinition StructuredField(
        string name, int bitOffset, int bitLength = 8) =>
        new(name, name, "", bitOffset, bitLength, "A_UINT8", "km/h",
            null, null, 1, 0, false, "BIG_ENDIAN");

    [Fact]
    public void OverlappingCddOdxDidSubfieldsCannotFeedNumericCanCorrelation()
    {
        var result = DecodeStructuredDid(
            [StructuredField("Speed", 0), StructuredField("Pressure", 0)],
            [0x2A]);

        Assert.False(result.DidPayloadFullyDecoded);
        Assert.Contains("overlapping", result.ResponseSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, result.Values.Count);
        Assert.All(result.Values, value =>
        {
            Assert.Null(value.NumericValue);
            Assert.Null(value.ResponseBitOffset);
            Assert.Equal(value.Raw, value.Physical);
            Assert.Contains("Overlapping", value.Meaning);
        });
    }

    [Fact]
    public void SparseCddOdxDidLayoutNeverClaimsEveryResponseBitWasDecoded()
    {
        var result = DecodeStructuredDid(
            [StructuredField("Speed", 0), StructuredField("Pressure", 16)],
            [0x2A, 0xCC, 0x09]);

        Assert.False(result.DidPayloadFullyDecoded);
        Assert.Contains("8 unmapped bit(s)", result.ResponseSummary);
        Assert.Equal(2, result.Values.Count);
        // Non-overlapping subfields may still be inspected independently.
        Assert.Equal(new double?[] { 42, 9 },
            result.Values.Select(value => value.NumericValue).ToArray());
    }

    [Fact]
    public void CompleteNonOverlappingDidFieldsStillDecodeNormally()
    {
        var result = DecodeStructuredDid(
            [StructuredField("Speed", 0), StructuredField("Pressure", 8)],
            [0x2A, 0x09]);

        Assert.True(result.DidPayloadFullyDecoded);
        Assert.Equal(new double?[] { 42, 9 },
            result.Values.Select(value => value.NumericValue).ToArray());
    }
}
