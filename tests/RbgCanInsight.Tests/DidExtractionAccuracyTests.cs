using RbgCanInsight.Correlation;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DidExtractionAccuracyTests
{
    private static CanFrame Frame(byte[] bytes) =>
        new(0, 0x7E8, bytes, 1_000_000);

    private static DidParameter Did(
        string ecu = "ECM", string datatype = "A_UINT16", int bits = 16,
        IReadOnlyList<DiagnosticFieldDefinition>? fields = null, double factor = 1) =>
        new(0xF405, "EngineCoolantTemperature", "Coolant", "Temperature", "degC",
            null, null, factor, 0, datatype, bits, ecu, "synthetic.cdd", fields);

    [Fact]
    public void SignedUdsValueUsesTwosComplement()
    {
        var samples = IsoTpDidExtractor.Summarize(
            [Frame([0x05, 0x62, 0xF4, 0x05, 0xFF, 0x80])],
            [Did(datatype: "A_INT16", factor: 0.5)]);
        var value = Assert.Single(samples.Values);
        Assert.Equal(-64, value.LastValue);
    }

    [Fact]
    public void SingleScalarFieldUsesDeclaredByteOrderAndOffset()
    {
        var field = new DiagnosticFieldDefinition(
            "Temp", "Temperature", "", 8, 16, "A_UINT16", "degC",
            null, null, 1, 0, false, "LITTLE_ENDIAN");
        var samples = IsoTpDidExtractor.Summarize(
            [Frame([0x06, 0x62, 0xF4, 0x05, 0x99, 0x34, 0x12])],
            [Did(fields: [field])]);
        Assert.Equal(0x1234, Assert.Single(samples.Values).LastValue);
    }

    [Fact]
    public void NewCanFdSingleFrameSupersedesUnfinishedDidMultiFrameResponse()
    {
        // 8-byte UDS 0x62 response with a genuine 40-bit scalar; CAN FD
        // single-frame escape carries it whole, while a late classic CF must
        // not resurrect an earlier interrupted multi-frame transfer.
        var frames = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x10, 0x08, 0x62, 0xF4, 0x05, 0x00, 0x00, 0x00], 0),
            new CanFrame(0, 0x7E8,
                [0x00, 0x08, 0x62, 0xF4, 0x05, 0x00, 0x00, 0x00, 0x00, 0x28, 0, 0],
                1_000_000, CanFrameFlags.FlexibleDataRate),
            new CanFrame(0, 0x7E8, [0x21, 0x00, 0x50, 0, 0, 0, 0, 0], 2_000_000)
        };

        var summary = Assert.Single(IsoTpDidExtractor.Summarize(frames, [Did(bits: 40)]).Values);
        Assert.Equal(1, summary.SampleCount);
        Assert.Equal(40, summary.LastValue);
    }

    [Fact]
    public void SingleFrameEscapeIsAcceptedOnlyForCanFd()
    {
        var bytes = new byte[] { 0x00, 0x05, 0x62, 0xF4, 0x05, 0x00, 0x50, 0x00 };
        var classicCan = new CanFrame(0, 0x7E8, bytes, 0);
        var canFd = new CanFrame(0, 0x7E8, bytes, 0, CanFrameFlags.FlexibleDataRate);

        Assert.Empty(IsoTpDidExtractor.Summarize([classicCan], [Did()]));
        Assert.Equal(80, Assert.Single(IsoTpDidExtractor.Summarize([canFd], [Did()]).Values).LastValue);
    }

    [Fact]
    public void DelayedConsecutiveFrameCannotInventAScalarDidMeasurement()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x10, 0x08, 0x62, 0xF4, 0x05, 0x00, 0x00, 0x00], 0),
            new CanFrame(0, 0x7E8, [0x21, 0x00, 0x50, 0, 0, 0, 0, 0],
                6_000_000_000L)
        };
        Assert.Empty(IsoTpDidExtractor.Summarize(frames, [Did(bits: 40)]));
    }

    [Fact]
    public void CorruptReplacementFirstFrameCannotCompleteOldDidMeasurement()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x10, 0x08, 0x62, 0xF4, 0x05, 0x00, 0x00, 0x00], 0),
            new CanFrame(0, 0x7E8,
                [0x10, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0], 1_000_000),
            new CanFrame(0, 0x7E8,
                [0x21, 0x00, 0x50, 0, 0, 0, 0, 0], 2_000_000)
        };
        Assert.Empty(IsoTpDidExtractor.Summarize(frames, [Did(bits: 40)]));
    }

    [Fact]
    public void ValidMultiFrameDidScalarStillMatchesReassembledSourceBytes()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x10, 0x08, 0x62, 0xF4, 0x05, 0x00, 0x00, 0x00], 0),
            new CanFrame(0, 0x7E8, [0x21, 0x00, 0x50, 0, 0, 0, 0, 0],
                4_000_000_000L)
        };
        var summary = Assert.Single(IsoTpDidExtractor.Summarize(
            frames, [Did(bits: 40)]).Values);
        Assert.Equal(1, summary.SampleCount);
        Assert.Equal(80, summary.LastValue);
    }

    [Fact]
    public void IndependentLoggersCannotCompleteEachOthersIsoTpResponse()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8, [0x10, 0x08, 0x62, 0xF4, 0x05, 0x00, 0x00, 0x00], 0),
            new CanFrame(0, 0x7E8, [0x21, 0x00, 0x50, 0, 0, 0, 0, 0], 1_000_000)
        };
        Assert.Empty(IsoTpDidExtractor.Summarize(frames, [Did(bits: 40)], index => index));
        Assert.Empty(IsoTpDidExtractor.Summarize(frames, [Did(bits: 40)], index => index == 1 ? -1 : 0));
        var sameSource = IsoTpDidExtractor.Summarize(frames, [Did(bits: 40)], _ => 0);
        Assert.Equal(80, Assert.Single(sameSource.Values).LastValue);
    }

    [Fact]
    public void IndependentCompleteResponsesAreNotMergedIntoOneDidSummary()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8, [0x05, 0x62, 0xF4, 0x05, 0x00, 0x50], 0),
            new CanFrame(0, 0x7E8, [0x05, 0x62, 0xF4, 0x05, 0x00, 0x60], 1_000_000)
        };
        Assert.Empty(IsoTpDidExtractor.Summarize(frames, [Did()], index => index));
        Assert.Equal(2, Assert.Single(IsoTpDidExtractor.Summarize(frames, [Did()], _ => 0).Values).SampleCount);
    }

    [Fact]
    public void TruncatedScalarDoesNotFabricateNumericEvidence()
    {
        var samples = IsoTpDidExtractor.Summarize(
            [Frame([0x04, 0x62, 0xF4, 0x05, 0xFF])], [Did()]);
        Assert.Empty(samples);
    }

    [Fact]
    public void RawOnlyScalarFieldNeverProducesPhysicalPreview()
    {
        var field = new DiagnosticFieldDefinition(
            "RawFlag", "Raw flag", "", 0, 8, "A_UINT8", "",
            null, null, 1, 0, false, "HIGH_LOW",
            SupportsPhysicalConversion: false);
        Assert.Empty(IsoTpDidExtractor.Summarize(
            [Frame([0x04, 0x62, 0xF4, 0x05, 0x42])],
            [Did(fields: [field], bits: 8)]));
    }

    [Fact]
    public void ConflictingDidLayoutsNeverBecomeScalarSamples()
    {
        var conflicting = Did(bits: 8) with
        {
            DefinitionIssues = ["Conflicting positive-response structures"]
        };
        Assert.Empty(IsoTpDidExtractor.Summarize(
            [Frame([0x04, 0x62, 0xF4, 0x05, 0x42])],
            [conflicting]));
    }

    [Fact]
    public void UnknownTrailingResponseBytesAreNotSilentlyIgnored()
    {
        Assert.Empty(IsoTpDidExtractor.Summarize(
            [Frame([0x05, 0x62, 0xF4, 0x05, 0x42, 0x99])],
            [Did(bits: 8)]));
    }

    [Fact]
    public void WideRawIntegerIsNotMisrepresentedAsExactDouble()
    {
        var fdFrame = new CanFrame(0, 0x7E8,
            [0x00, 0x0B, 0x62, 0xF4, 0x05,
             0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
             0x00, 0x00, 0x00],
            1_000_000, CanFrameFlags.FlexibleDataRate);
        Assert.Empty(IsoTpDidExtractor.Summarize([fdFrame], [Did(bits: 64)]));
    }

    [Fact]
    public void SameDidOnTwoEcusRemainsUnattributedWithoutEndpointEvidence()
    {
        var samples = IsoTpDidExtractor.Summarize(
            [Frame([0x05, 0x62, 0xF4, 0x05, 0x00, 0x50])],
            [Did("ECM"), Did("BCM")]);
        Assert.Empty(samples);
    }
}
