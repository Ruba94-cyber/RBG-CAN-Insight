using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class IsoTpSourceByteIndexTests
{
    [Fact]
    public void SingleFrameDidMapsExactResponseBytesAndBitInterval()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x05, 0x62, 0xF4, 0x05, 0x34, 0x12, 0, 0], 100)
        };
        var pdu = Assert.Single(IsoTpReassembler.Reassemble(frames));
        Assert.True(IsoTpSourceByteIndex.TryBuild(frames, pdu, out var source));
        Assert.NotNull(source);
        Assert.Equal(5, source.PayloadLength);

        Assert.True(source.TryMap(24, 16, out var spans));
        var span = Assert.Single(spans);
        Assert.Equal(0, span.FrameIndex);
        Assert.Equal(4, span.CanPayloadByteOffset);
        Assert.Equal(3, span.ResponsePayloadByteOffset);
        Assert.Equal(2, span.ByteCount);
        Assert.Contains("CAN frame #1", span.DisplayText);

        Assert.True(source.TryMap(26, 3, out var packed));
        Assert.Equal(4, Assert.Single(packed).CanPayloadByteOffset);
        Assert.False(source.TryMap(39, 2, out _)); // past PDU, not padded CAN bytes
    }

    [Fact]
    public void MultiFrameDidFieldAcrossFirstAndConsecutiveFramesPreservesBothSources()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x10, 0x0B, 0x62, 0xF4, 0x05, 0x11, 0x22, 0x33], 0),
            new CanFrame(0, 0x7E8,
                [0x21, 0x44, 0x55, 0x66, 0x77, 0x88, 0, 0], 1_000_000)
        };
        var pdu = Assert.Single(IsoTpReassembler.Reassemble(frames));
        Assert.Equal(new[] { 0, 1 }, pdu.FrameIndices);
        Assert.True(IsoTpSourceByteIndex.TryBuild(frames, pdu, out var source));
        Assert.NotNull(source);

        Assert.True(source.TryMap(24, 64, out var spans));
        Assert.Equal(2, spans.Count);
        Assert.Equal(new DiagnosticSourceByteSpan(0, 5, 3, 3), spans[0]);
        Assert.Equal(new DiagnosticSourceByteSpan(1, 1, 6, 5), spans[1]);
    }

    [Fact]
    public void CanFdEscapeSingleFrameMapsAfterBothPciBytes()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x00, 0x04, 0x62, 0xF1, 0x90, 0x7F, 0, 0, 0, 0, 0, 0],
                0, CanFrameFlags.FlexibleDataRate)
        };
        var pdu = Assert.Single(IsoTpReassembler.Reassemble(frames));
        Assert.True(IsoTpSourceByteIndex.TryBuild(frames, pdu, out var source));
        Assert.NotNull(source);
        Assert.True(source.TryMap(24, 8, out var spans));
        Assert.Equal(new DiagnosticSourceByteSpan(0, 5, 3, 1),
            Assert.Single(spans));
    }

    [Fact]
    public void FdSingleFrameUsesEscapeHeaderOnlyWhenDlcExceedsEight()
    {
        // An FD 12-byte frame with a nonzero 4-bit PCI length is not
        // the canonical CAN FD ISO-TP single-frame encoding. A lenient
        // reassembler may display it, but may NOT certify its bytes.
        var noncanonicalFd12 = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x09, 0x62, 0xF1, 0x90, 0x31, 0x32, 0x33, 0x34,
                 0x35, 0x36, 0, 0], 100,
                CanFrameFlags.FlexibleDataRate)
        };
        var decoded = Assert.Single(IsoTpReassembler.Reassemble(noncanonicalFd12));
        Assert.False(IsoTpSourceByteIndex.TryBuild(noncanonicalFd12, decoded, out _));

        // Eight-byte FD records instead use the classic 4-bit SF_DL;
        // escape is not required and is not source-certified.
        var unnecessaryEscapeFd8 = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x00, 0x04, 0x62, 0xF1, 0x90, 0x31, 0, 0], 100,
                CanFrameFlags.FlexibleDataRate)
        };
        decoded = Assert.Single(IsoTpReassembler.Reassemble(unnecessaryEscapeFd8));
        Assert.False(IsoTpSourceByteIndex.TryBuild(unnecessaryEscapeFd8, decoded, out _));

        var canonicalFd8 = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x04, 0x62, 0xF1, 0x90, 0x31, 0, 0, 0], 100,
                CanFrameFlags.FlexibleDataRate)
        };
        decoded = Assert.Single(IsoTpReassembler.Reassemble(canonicalFd8));
        Assert.True(IsoTpSourceByteIndex.TryBuild(canonicalFd8, decoded, out _));
    }

    [Fact]
    public void NoByteMappingForIncompleteModifiedOrWrongBusEvidence()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x05, 0x62, 0xF4, 0x05, 0x34, 0x12, 0, 0], 0)
        };
        var pdu = Assert.Single(IsoTpReassembler.Reassemble(frames));
        Assert.False(IsoTpSourceByteIndex.TryBuild(frames,
            pdu with { IsComplete = false }, out _));
        Assert.False(IsoTpSourceByteIndex.TryBuild(frames,
            pdu with { Payload = [0x62, 0xF4, 0x05, 0x35, 0x12] }, out _));
        Assert.False(IsoTpSourceByteIndex.TryBuild(frames,
            pdu with { FrameIndices = [1] }, out _));
        Assert.False(IsoTpSourceByteIndex.TryBuild(frames,
            pdu with { Channel = 1 }, out _));
        Assert.False(IsoTpSourceByteIndex.TryBuild(frames,
            pdu with { IsExtended = true }, out _));
        Assert.False(IsoTpSourceByteIndex.TryBuild(frames,
            pdu with { StartTimestampNanoseconds = 1 }, out _));
    }

    [Fact]
    public void ExtremelyLongTransportSourceListCannotAllocateUnboundedEvidenceSpans()
    {
        var frames = new[]
        {
            new CanFrame(0, 0x7E8, [0x04, 0x62, 0xF1, 0x90, 0x12, 0, 0, 0], 0)
        };
        var pdu = Assert.Single(IsoTpReassembler.Reassemble(frames));
        var oversized = pdu with
        {
            FrameIndices = Enumerable.Repeat(0, 65_537).ToArray()
        };
        Assert.False(IsoTpSourceByteIndex.TryBuild(frames, oversized, out _));
    }

    [Fact]
    public void NormalAddressingSourceProofRejectsClassicSfLengthEscapeAndBadFdDlc()
    {
        var classicEscape = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x00, 0x04, 0x62, 0xF1, 0x90, 0x30, 0, 0], 0)
        };
        var classicPdu = Assert.Single(IsoTpReassembler.Reassemble(classicEscape));
        Assert.False(IsoTpSourceByteIndex.TryBuild(classicEscape, classicPdu, out _));

        var malformedFd = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x00, 0x04, 0x62, 0xF1, 0x90, 0x30, 0, 0, 0, 0],
                0, CanFrameFlags.FlexibleDataRate)
        };
        var fdPdu = Assert.Single(IsoTpReassembler.Reassemble(malformedFd));
        Assert.False(IsoTpSourceByteIndex.TryBuild(malformedFd, fdPdu, out _));

        var brsWithoutFd = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x04, 0x62, 0xF1, 0x90, 0x30, 0, 0, 0],
                0, CanFrameFlags.BitRateSwitch)
        };
        var brsPdu = Assert.Single(IsoTpReassembler.Reassemble(brsWithoutFd));
        Assert.False(IsoTpSourceByteIndex.TryBuild(brsWithoutFd, brsPdu, out _));
    }

    [Fact]
    public void IntermediateConsecutiveFrameTimeCannotJumpBeyondTransportTimeout()
    {
        var original = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x10, 0x13, 0x62, 0xF1, 0x90, 0x11, 0x22, 0x33], 0),
            new CanFrame(0, 0x7E8,
                [0x21, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA],
                1_000_000_000),
            new CanFrame(0, 0x7E8,
                [0x22, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11],
                2_000_000_000)
        };
        var pdu = Assert.Single(IsoTpReassembler.Reassemble(original));
        Assert.True(pdu.IsComplete);
        Assert.True(IsoTpSourceByteIndex.TryBuild(original, pdu, out _));
        var tamperedMiddle = new[]
        {
            original[0],
            new CanFrame(0, 0x7E8, original[1].GetPayload(),
                7_000_000_000),
            original[2]
        };
        Assert.False(IsoTpSourceByteIndex.TryBuild(tamperedMiddle, pdu, out _));
        Assert.False(IsoTpSourceByteIndex.TryBuild(original,
            pdu with { TransportIssue = "sequence not proven" }, out _));
    }

    [Fact]
    public void WrongConsecutiveSequenceCannotClaimVerifiedSourceBytes()
    {
        var original = new[]
        {
            new CanFrame(0, 0x7E8,
                [0x10, 0x0B, 0x62, 0xF4, 0x05, 0x11, 0x22, 0x33], 0),
            new CanFrame(0, 0x7E8,
                [0x21, 0x44, 0x55, 0x66, 0x77, 0x88, 0, 0], 1_000_000)
        };
        var pdu = Assert.Single(IsoTpReassembler.Reassemble(original));
        var modified = new[]
        {
            original[0],
            new CanFrame(0, 0x7E8,
                [0x22, 0x44, 0x55, 0x66, 0x77, 0x88, 0, 0], 1_000_000)
        };
        Assert.False(IsoTpSourceByteIndex.TryBuild(modified, pdu, out _));
    }
}
