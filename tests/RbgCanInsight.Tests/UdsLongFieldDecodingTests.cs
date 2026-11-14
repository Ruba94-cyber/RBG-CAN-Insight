using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Large CDD/ODX DID text/array fields must remain inspectable and
/// must never acquire a fabricated numeric physical conversion.</summary>
public sealed class UdsLongFieldDecodingTests
{
    private static DiagnosticFieldDefinition Field(string kind, int offset, int bits) =>
        new("Value", "Value", "Synthetic long field", offset, bits, kind,
            "", null, null, null, null, false, "BIG_ENDIAN");

    private static DidCatalog Catalog(DiagnosticFieldDefinition field) =>
        new("synthetic", "ECM", "synthetic.odx",
            [new DidParameter(0xF190, "Identifier", "Identifier", "", "",
                null, null, null, null, field.DataType, field.BitLength,
                "ECM", "synthetic.odx", [field])],
            Connections:
            [
                new DiagnosticConnection("ECM", 0x7E0, 0x7E8, false,
                    "normal", "ECM", "synthetic.odx")
            ]);

    private static DecodedUdsOperation Decode(DiagnosticFieldDefinition field, byte[] bytes)
    {
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
            [0x22, 0xF1, 0x90], true, "");
        var response = new IsoTpMessage(0, 0x7E8, false, 1, 1, [1],
            [0x62, 0xF1, 0x90, .. bytes], true, "");
        return UdsServiceDecoder.Decode(request, [response], [Catalog(field)], "ECM");
    }

    [Fact]
    public void SeventeenCharacterAsciiVinLongerThan64BitsRemainsDecodedAsText()
    {
        const string vin = "TESTVIN0000000001";
        var result = Decode(Field("A_ASCIISTRING", 0, 17 * 8),
            System.Text.Encoding.ASCII.GetBytes(vin));
        Assert.True(result.DidPayloadFullyDecoded);
        var value = Assert.Single(result.Values);
        Assert.Equal(vin, value.Physical);
        Assert.Null(value.NumericValue);
        Assert.Equal(24, value.ResponseBitOffset);
        Assert.Equal(136, value.ResponseBitLength);
    }

    [Fact]
    public void WideIntegerRetainsExactRawBytesWithoutFabricatedDoubleValue()
    {
        var value = Assert.Single(Decode(Field("A_UINT72", 0, 72),
            [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFF]).Values);
        Assert.Null(value.NumericValue);
        Assert.Equal(value.Raw, value.Physical);
        Assert.Equal(72, value.ResponseBitLength);
        Assert.Contains("exceeds 64 bits", value.Meaning);
    }

    [Fact]
    public void NonAsciiBytesAndUnalignedTextAreShownAsRawNotGuessedCharacters()
    {
        var nonAscii = Assert.Single(Decode(Field("A_ASCIISTRING", 0, 24),
            [0x41, 0xFF, 0x42]).Values);
        Assert.Equal(nonAscii.Raw, nonAscii.Physical);
        Assert.Null(nonAscii.NumericValue);
        Assert.Contains("ASCII interpretation unavailable", nonAscii.Meaning);

        var unaligned = Assert.Single(Decode(Field("A_ASCIISTRING", 1, 16),
            [0x41, 0x42, 0x43]).Values);
        Assert.Equal(unaligned.Raw, unaligned.Physical);
        Assert.Null(unaligned.NumericValue);
        Assert.Equal(25, unaligned.ResponseBitOffset);
    }

    [Fact]
    public void AsciiTextTrimsOnlyTrailingZeroPaddingAndKeepsRawEvidence()
    {
        var result = Decode(Field("A_ASCIISTRING", 0, 48),
            [0x41, 0x42, 0x43, 0x00, 0x00, 0x00]);
        var value = Assert.Single(result.Values);
        Assert.Equal("ABC", value.Physical);
        Assert.Equal("41 42 43 00 00 00", value.Raw);
        Assert.Null(value.NumericValue);
        Assert.True(result.DidPayloadFullyDecoded);
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0x00, 0x42 })]
    [InlineData(new byte[] { 0x41, 0x0D, 0x42 })]
    [InlineData(new byte[] { 0x41, 0x0A, 0x42 })]
    [InlineData(new byte[] { 0x41, 0x7F, 0x42 })]
    public void EmbeddedAsciiControlsCannotBecomeConfidentDecodedText(byte[] payload)
    {
        var value = Assert.Single(
            Decode(Field("A_ASCIISTRING", 0, 24), payload).Values);
        Assert.Null(value.NumericValue);
        Assert.Equal(value.Raw, value.Physical);
        Assert.Contains("ASCII interpretation unavailable", value.Meaning);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(64)]
    [InlineData(4096)]
    public void LongRawByteArrayKeepsEveryByteInCanonicalHexOrder(int byteCount)
    {
        var bytes = Enumerable.Range(0, byteCount)
            .Select(index => (byte)((index * 37 + 11) & 0xFF)).ToArray();
        var decoded = Decode(Field("A_BYTEFIELD", 0, byteCount * 8), bytes);
        Assert.True(decoded.DidPayloadFullyDecoded);
        var field = Assert.Single(decoded.Values);
        var reference = string.Join(' ', bytes.Select(value => value.ToString("X2")));
        Assert.Equal(reference, field.Raw);
        Assert.Equal(reference, field.Physical);
        Assert.Null(field.NumericValue);
        Assert.Equal(byteCount * 3 - 1, field.Raw.Length);
        Assert.Equal(byteCount * 8, field.ResponseBitLength);
    }

    private static DecodedUdsOperation DecodePlainDid(
        string? dataType, byte[] bytes)
    {
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0, [0],
            [0x22, 0xF1, 0x90], true, "");
        var response = new IsoTpMessage(0, 0x7E8, false, 1, 1, [1],
            [0x62, 0xF1, 0x90, .. bytes], true, "");
        IReadOnlyList<DidCatalog> catalogs = dataType is null
            ? []
            : [new DidCatalog("synthetic", "ECM", "synthetic.odx",
                [new DidParameter(0xF190, "Plain", "Plain", "", "",
                    null, null, null, null, dataType, bytes.Length * 8,
                    "ECM", "synthetic.odx")])];
        return UdsServiceDecoder.Decode(request, [response], catalogs,
            dataType is null ? null : "ECM");
    }

    [Fact]
    public void PlainAsciiDidUsesSameControlCharacterGateAsNestedField()
    {
        var decoded = DecodePlainDid("A_ASCIISTRING",
            [0x41, 0x42, 0x43, 0x00, 0x00, 0x00]);
        Assert.True(decoded.DidPayloadFullyDecoded);
        var value = Assert.Single(decoded.Values);
        Assert.Equal("ABC", value.Physical);
        Assert.Equal("41 42 43 00 00 00", value.Raw);
        Assert.Null(value.NumericValue);

        var invalid = DecodePlainDid("A_ASCIISTRING",
            [0x41, 0x0D, 0x42]);
        var invalidField = Assert.Single(invalid.Values);
        Assert.Equal("41 0D 42", invalidField.Raw);
        Assert.Equal(invalidField.Raw, invalidField.Physical);
        Assert.Null(invalidField.NumericValue);
    }

    [Fact]
    public void UnknownDidPrintableBytesStayRawInsteadOfBeingGuessedAsText()
    {
        var result = DecodePlainDid(null, [0x41, 0x42, 0x43]);
        Assert.False(result.DidPayloadFullyDecoded);
        var value = Assert.Single(result.Values);
        Assert.Equal("41 42 43", value.Raw);
        Assert.Equal(value.Raw, value.Physical);
        Assert.Null(value.NumericValue);
    }

    [Fact]
    public void ActualVinCrossingThreeOriginalCanFramesKeepsAllSourceByteSpans()
    {
        const string vin = "TESTVIN0000000001";
        var data = System.Text.Encoding.ASCII.GetBytes(vin);
        byte[] payload = [0x62, 0xF1, 0x90, .. data];
        Assert.Equal(20, payload.Length);
        var frames = new[]
        {
            new CanFrame(0, 0x7E0, [0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8,
                [0x10, 0x14, .. payload.AsSpan(0, 6).ToArray()], 1_000_000),
            new CanFrame(0, 0x7E8,
                [0x21, .. payload.AsSpan(6, 7).ToArray()], 2_000_000),
            new CanFrame(0, 0x7E8,
                [0x22, .. payload.AsSpan(13, 7).ToArray()], 3_000_000)
        };
        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(frames,
            [Catalog(Field("A_ASCIISTRING", 0, 136))]));
        Assert.Equal(DiagnosticOutcome.Positive, tx.Outcome);
        Assert.True(tx.Decoded.DidPayloadFullyDecoded);
        var value = Assert.Single(tx.Decoded.Values);
        Assert.Equal(vin, value.Physical);
        Assert.True(value.HasVerifiedSource);
        Assert.Equal(new[]
        {
            new DiagnosticSourceByteSpan(1, 5, 3, 3),
            new DiagnosticSourceByteSpan(2, 1, 6, 7),
            new DiagnosticSourceByteSpan(3, 1, 13, 7)
        }, value.SourceSpans);
        Assert.Null(value.NumericValue);
    }
}
