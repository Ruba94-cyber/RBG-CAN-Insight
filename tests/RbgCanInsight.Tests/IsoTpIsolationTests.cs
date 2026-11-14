using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class IsoTpIsolationTests
{
    private static CanFrame Frame(uint id, bool extended, byte[] payload, long timestamp) =>
        new(0, id, payload, timestamp, extended ? CanFrameFlags.Extended : CanFrameFlags.None);

    [Fact]
    public void SameNumericIdentifierOnStandardAndExtendedBusDoesNotMixAssemblies()
    {
        const uint id = 0x321;
        var frames = new[]
        {
            Frame(id, false, [0x10, 0x0B, 0x62, 0xF1, 0x90, 0x10, 0x11, 0x12], 0),
            Frame(id, true, [0x10, 0x0B, 0x62, 0xF1, 0x87, 0x20, 0x21, 0x22], 1_000_000),
            Frame(id, false, [0x21, 0x13, 0x14, 0x15, 0x16, 0x17], 2_000_000),
            Frame(id, true, [0x21, 0x23, 0x24, 0x25, 0x26, 0x27], 3_000_000)
        };

        var pdus = IsoTpReassembler.Reassemble(frames);
        Assert.Equal(2, pdus.Count);
        Assert.All(pdus, pdu => Assert.True(pdu.IsComplete));
        Assert.Contains(pdus, pdu => !pdu.IsExtended && pdu.Payload[2] == 0x90);
        Assert.Contains(pdus, pdu => pdu.IsExtended && pdu.Payload[2] == 0x87);
    }

    [Fact]
    public void StaleConsecutiveFrameDoesNotCompleteOldPdu()
    {
        var frames = new[]
        {
            Frame(0x7E8, false, [0x10, 0x0B, 0x62, 0xF1, 0x90, 1, 2, 3], 0),
            Frame(0x7E8, false, [0x21, 4, 5, 6, 7, 8], 6_000_000_000L)
        };
        var pdu = Assert.Single(IsoTpReassembler.Reassemble(frames));
        Assert.False(pdu.IsComplete);
        Assert.Contains("timeout", pdu.TransportIssue);
    }

    [Fact]
    public void FastPciPrecheckPreservesRealPduAndOriginalFrameIndexAmongNoise()
    {
        var frames = new[]
        {
            Frame(0x100, false, [0xF0, 1, 2, 3, 4, 5, 6, 7], 0),
            Frame(0x110, false, [0x30, 0, 0, 0, 0, 0, 0, 0], 1_000_000),
            Frame(0x7E8, false, [0x21, 1, 2, 3, 4, 5, 6, 7], 2_000_000),
            Frame(0x7E8, false, [0x04, 0x62, 0xF4, 0x05, 0x28, 0, 0, 0],
                3_000_000),
            Frame(0x120, false, [0xC0, 1, 2, 3, 4, 5, 6, 7], 4_000_000)
        };
        var pdu = Assert.Single(IsoTpReassembler.Reassemble(frames));
        Assert.True(pdu.IsComplete);
        Assert.Equal(new[] { 3 }, pdu.FrameIndices);
        Assert.Equal(new byte[] { 0x62, 0xF4, 0x05, 0x28 }, pdu.Payload);
        Assert.Equal(3_000_000, pdu.StartTimestampNanoseconds);
    }

    [Fact]
    public void ANewSingleFrameAbortsAnOlderIncompleteTransferOnTheSameSource()
    {
        var frames = new[]
        {
            Frame(0x7E8, false, [0x10, 0x0B, 0x62, 0xF1, 0x90, 1, 2, 3], 0),
            Frame(0x7E8, false, [0x03, 0x7F, 0x22, 0x13], 1_000_000),
            // Must not complete the OLD 0x62 response after the intervening SF.
            Frame(0x7E8, false, [0x21, 4, 5, 6, 7, 8], 2_000_000)
        };

        var messages = IsoTpReassembler.Reassemble(frames);
        Assert.Equal(2, messages.Count);
        var aborted = Assert.Single(messages.Where(message => !message.IsComplete));
        Assert.Contains("new single frame", aborted.TransportIssue);
        Assert.Equal(new[] { 0 }, aborted.FrameIndices);
        var single = Assert.Single(messages.Where(message => message.IsComplete));
        Assert.Equal(new byte[] { 0x7F, 0x22, 0x13 }, single.Payload);
        Assert.Equal(new[] { 1 }, single.FrameIndices);
    }

    [Fact]
    public void SamePhysicalCanIdOnAnotherBusDoesNotAbortActiveTransfer()
    {
        var frames = new[]
        {
            Frame(0x7E8, false, [0x10, 0x0B, 0x62, 0xF1, 0x90, 1, 2, 3], 0),
            new CanFrame(1, 0x7E8, [0x02, 0x50, 0x03], 1_000_000),
            Frame(0x7E8, false, [0x21, 4, 5, 6, 7, 8], 2_000_000)
        };

        var messages = IsoTpReassembler.Reassemble(frames);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.True(message.IsComplete));
        Assert.Equal(new[] { 0, 2 },
            Assert.Single(messages.Where(message => message.Channel == 0)).FrameIndices);
        Assert.Equal(new[] { 1 },
            Assert.Single(messages.Where(message => message.Channel == 1)).FrameIndices);
    }

    [Fact]
    public void InvalidReplacementFirstFrameCannotLeaveOldTransportSequenceLive()
    {
        var frames = new[]
        {
            Frame(0x7E8, false, [0x10, 0x0B, 0x62, 0xF1, 0x90, 1, 2, 3], 0),
            Frame(0x7E8, false, [0x10, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0],
                1_000_000),
            Frame(0x7E8, false, [0x21, 4, 5, 6, 7, 8], 2_000_000)
        };
        var aborted = Assert.Single(IsoTpReassembler.Reassemble(frames));
        Assert.False(aborted.IsComplete);
        Assert.Contains("invalid first frame", aborted.TransportIssue);
        Assert.Equal(new[] { 0, 1 }, aborted.FrameIndices);
    }

    [Fact]
    public void ImplausibleExtendedLengthCannotCreateAnUnboundedIsoTpAssembly()
    {
        var maliciousLength = Frame(0x7E8, false,
            [0x10, 0x00, 0x01, 0x10, 0x00, 0x00, 0x62, 0xF1], 0);
        Assert.Empty(IsoTpReassembler.Reassemble([maliciousLength]));
    }

    [Fact]
    public void ErrorAndRemoteFramesAreNotDecodedAsIsoTp()
    {
        var error = new CanFrame(0, 0x7E8, [0x03, 0x62, 0xF1, 0x90], 0, CanFrameFlags.Error);
        var remote = new CanFrame(0, 0x7E8, [], 1, CanFrameFlags.Remote,
            declaredDataLength: 4);
        Assert.Empty(IsoTpReassembler.Reassemble([error, remote]));
    }
}
