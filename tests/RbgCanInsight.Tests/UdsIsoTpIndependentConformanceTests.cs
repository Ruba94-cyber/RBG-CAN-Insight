using System.Globalization;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

// =====================================================================================
// Independently derived conformance vectors.
//
// Every expected value below was derived BY HAND from the standards, never by running
// the application:
//   ISO 14229-1 (UDS)      - service identifiers (Table 23), positive response SID = SID + 0x40
//                            (clause 7.2 / 8.7), negative response format 0x7F SID NRC (8.7),
//                            suppressPosRspMsgIndicationBit (8.2.2), NRC table (Annex A.1),
//                            DTC status byte (Annex D.2), response pending 0x78 / P2*Server (8.8).
//   ISO 15765-2 (ISO-TP)   - N_PCI (clause 9.6): SF 0x0, FF 0x1, CF 0x2, FC 0x3;
//                            SF_DL (9.6.2), FF_DL (9.6.3), SN (9.6.4), FS/BS/STmin (9.6.5),
//                            timing parameters N_Bs / N_Cr (clause 9.7, Table 17 = 1000 ms),
//                            addressing formats (clause 10).
//   ISO 15765-4 (OBD CAN)  - 0x7DF functional, 0x7E0+n physical request, 0x7E8+n response.
// The frame builders in this file implement the wire layouts of ISO 15765-2 literally and
// share no code with the product.
// =====================================================================================

internal static class Iso
{
    public const long Ms = 1_000_000L;

    public static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    public static CanFrame F(uint id, string hex, long tMs, int channel = 0,
        CanFrameFlags flags = CanFrameFlags.None) =>
        new(channel, id, H(hex), tMs * Ms, flags);

    public static CanFrame Fns(uint id, string hex, long tNs, int channel = 0,
        CanFrameFlags flags = CanFrameFlags.None) =>
        new(channel, id, H(hex), tNs, flags);

    /// <summary>ISO 15765-2 clause 9.6 segmentation, classical CAN (8-byte frames) with optional
    /// padding of the unused tail (clause 6.5.2.? / 10.? - padding value is not interpreted).</summary>
    public static List<byte[]> SegmentClassic(byte[] pdu, byte? pad = 0xAA)
    {
        var frames = new List<byte[]>();
        if (pdu.Length <= 7)
        {
            frames.Add(Pad([(byte)pdu.Length, .. pdu], pad));
            return frames;
        }

        int offset;
        if (pdu.Length <= 4095)
        {
            frames.Add([(byte)(0x10 | (pdu.Length >> 8)), (byte)(pdu.Length & 0xFF), .. pdu[..6]]);
            offset = 6;
        }
        else
        {
            // FF_DL escape (9.6.3.2): 12-bit field 0, followed by a 32-bit FF_DL.
            frames.Add([0x10, 0x00,
                (byte)(pdu.Length >> 24), (byte)(pdu.Length >> 16), (byte)(pdu.Length >> 8), (byte)pdu.Length,
                pdu[0], pdu[1]]);
            offset = 2;
        }

        var sn = 1;
        while (offset < pdu.Length)
        {
            var take = Math.Min(7, pdu.Length - offset);
            frames.Add(Pad([(byte)(0x20 | (sn & 0x0F)), .. pdu[offset..(offset + take)]], pad));
            offset += take;
            sn++; // SN wraps 0xF -> 0x0 through the & 0x0F mask (9.6.4.3)
        }
        return frames;
    }

    /// <summary>CAN FD (64-byte) segmentation: SF escape (9.6.2.2), FF with 62 data bytes, CF with 63.</summary>
    public static List<byte[]> SegmentFd(byte[] pdu)
    {
        var frames = new List<byte[]>();
        if (pdu.Length <= 62)
        {
            frames.Add([0x00, (byte)pdu.Length, .. pdu]);
            return frames;
        }
        frames.Add([(byte)(0x10 | (pdu.Length >> 8)), (byte)(pdu.Length & 0xFF), .. pdu[..62]]);
        var offset = 62;
        var sn = 1;
        while (offset < pdu.Length)
        {
            var take = Math.Min(63, pdu.Length - offset);
            frames.Add([(byte)(0x20 | (sn & 0x0F)), .. pdu[offset..(offset + take)]]);
            offset += take;
            sn++;
        }
        return frames;
    }

    private static byte[] Pad(byte[] frame, byte? pad)
    {
        if (pad is null || frame.Length >= 8) return frame;
        var padded = new byte[8];
        Array.Fill(padded, pad.Value);
        frame.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>Append a PDU as classical ISO-TP frames, one frame per <paramref name="stepMs"/>.</summary>
    public static long Send(List<CanFrame> sink, uint id, string pduHex, long startMs,
        long stepMs = 1, byte? pad = 0xAA, int channel = 0, CanFrameFlags flags = CanFrameFlags.None)
    {
        var t = startMs;
        foreach (var bytes in SegmentClassic(H(pduHex), pad))
        {
            sink.Add(new CanFrame(channel, id, bytes, t * Ms, flags));
            t += stepMs;
        }
        return t - stepMs;
    }

    public static long Send(List<CanFrame> sink, uint id, byte[] pdu, long startMs,
        long stepMs = 1, byte? pad = 0xAA, int channel = 0)
    {
        var t = startMs;
        foreach (var bytes in SegmentClassic(pdu, pad))
        {
            sink.Add(new CanFrame(channel, id, bytes, t * Ms, CanFrameFlags.None));
            t += stepMs;
        }
        return t - stepMs;
    }
}

public sealed class IsoTpIndependentConformanceTests
{
    private const uint Tester = 0x7E0;
    private const uint Ecu = 0x7E8;

    private static IsoTpMessage Single(IEnumerable<CanFrame> frames) =>
        Assert.Single(IsoTpReassembler.Reassemble(frames.ToArray()));

    // ---------------- Single frame (ISO 15765-2 9.6.2) ----------------

    [Theory]
    [InlineData(0xAA)]
    [InlineData(0xCC)]
    [InlineData(0x00)]
    [InlineData(0x55)]
    public void SingleFramePaddingValueIsNeverPartOfThePdu(byte pad)
    {
        // N_PCI byte 0x05 = SF, SF_DL 5 -> exactly 5 bytes follow; the 2 trailing bytes are padding.
        var data = new byte[] { 0x05, 0x62, 0xF1, 0x90, 0x41, 0x42, pad, pad };
        var message = Single([new CanFrame(0, Ecu, data, 0)]);
        Assert.True(message.IsComplete);
        Assert.Equal(Iso.H("62F1904142"), message.Payload);
        Assert.Equal(new[] { 0 }, message.FrameIndices);
    }

    [Fact]
    public void SingleFrameMaximumClassicLengthIsSeven()
    {
        // SF_DL = 7 is the largest legal value in an 8 byte frame (8 - 1 PCI byte).
        var message = Single([Iso.F(Ecu, "07 62 F1 90 01 02 03 04", 0)]);
        Assert.Equal(Iso.H("62F190010203 04"), message.Payload);
    }

    [Fact]
    public void SingleFrameShorterThanItsDeclaredLengthIsNotAPdu()
    {
        // SF_DL 5 but only 3 data bytes in a 4 byte frame: invalid, must be ignored (9.6.2.1).
        Assert.Empty(IsoTpReassembler.Reassemble([Iso.F(Ecu, "05 62 F1 90", 0)]));
    }

    [Fact]
    public void SingleFrameLengthEightInClassicFrameIsIllegal()
    {
        // SF_DL (low nibble) = 8 cannot fit an 8-byte frame (needs 9) -> not a PDU.
        Assert.Empty(IsoTpReassembler.Reassemble([Iso.F(Ecu, "08 62 F1 90 01 02 03 04", 0)]));
    }

    [Fact]
    public void SingleFrameLengthZeroWithEightByteFrameIsNeverCertifiedAsSourceEvidence()
    {
        // ISO 15765-2:2016 9.6.2.1/9.6.2.2: the SF_DL escape byte exists only when CAN_DL > 8; in an
        // 8-byte frame SF_DL = 0 is reserved and a conforming receiver ignores the frame.
        // KNOWN, DELIBERATE DEVIATION (see IsoTpSourceByteIndexTests / UdsTransactionAnalyzerTests):
        // the reassembler still *displays* such a frame leniently, but the source-byte proof used
        // for every "verified original CAN bytes" claim must refuse it.
        var frames = new[] { Iso.F(Ecu, "00 05 62 F1 90 41 42 00", 0) };
        foreach (var message in IsoTpReassembler.Reassemble(frames))
            Assert.False(IsoTpSourceByteIndex.TryBuild(frames, message, out _));
    }

    [Fact]
    public void CanFdSingleFrameEscapeCarriesLengthInSecondByte()
    {
        // CAN FD, 13-byte payload frame: byte0 = 0x00 (SF, SF_DL=0 => escape), byte1 = SF_DL = 11.
        var pdu = Iso.H("62F190 0102030405060708");
        var frame = new CanFrame(0, Ecu, [0x00, (byte)pdu.Length, .. pdu], 0,
            CanFrameFlags.FlexibleDataRate | CanFrameFlags.BitRateSwitch);
        var message = Single([frame]);
        Assert.Equal(pdu, message.Payload);
    }

    [Fact]
    public void CanFdSingleFrameMaximumIs62Bytes()
    {
        // 64-byte frame - 2 header bytes = 62 data bytes.
        var pdu = Enumerable.Range(0, 62).Select(i => (byte)(0x62 + i)).ToArray();
        var message = Single([new CanFrame(0, Ecu, [0x00, 62, .. pdu], 0, CanFrameFlags.FlexibleDataRate)]);
        Assert.Equal(pdu, message.Payload);
    }

    // ---------------- First frame / consecutive frames (9.6.3, 9.6.4) ----------------

    [Fact]
    public void FirstFrame12BitLengthWithTwoConsecutiveFrames()
    {
        // FF_DL = 0x014 = 20: 6 bytes in FF, 7 in CF1, 7 in CF2 => 20, complete after CF2.
        var frames = new[]
        {
            Iso.F(Ecu, "10 14 62 F1 90 01 02 03", 0),
            Iso.F(Ecu, "21 04 05 06 07 08 09 0A", 2),
            Iso.F(Ecu, "22 0B 0C 0D 0E 0F 10 11", 3)
        };
        var message = Single(frames);
        Assert.True(message.IsComplete);
        Assert.Equal(Iso.H("62F190 01020304050607 08090A0B0C0D0E0F1011").Take(20).ToArray(), message.Payload);
        Assert.Equal(new[] { 0, 1, 2 }, message.FrameIndices);
        Assert.Equal(0, message.StartTimestampNanoseconds);
        Assert.Equal(3 * Iso.Ms, message.EndTimestampNanoseconds);
    }

    [Theory]
    [InlineData(0xAA)]
    [InlineData(0xCC)]
    [InlineData(0x00)]
    public void LastConsecutiveFramePaddingIsTrimmedUsingFirstFrameLength(byte pad)
    {
        // FF_DL = 18 => 6 + 7 + 5 bytes. CF2 carries 5 data bytes followed by 2 padding bytes.
        var frames = new[]
        {
            new CanFrame(0, Ecu, Iso.H("10 12 62 F1 90 01 02 03"), 0),
            new CanFrame(0, Ecu, Iso.H("21 04 05 06 07 08 09 0A"), Iso.Ms),
            new CanFrame(0, Ecu, [0x22, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, pad, pad], 2 * Iso.Ms)
        };
        var message = Single(frames);
        Assert.Equal(18, message.Payload.Length);
        Assert.Equal(0x0F, message.Payload[^1]);
        Assert.True(message.IsComplete);
    }

    [Fact]
    public void FirstFrameDeclaringLengthThatFitsInOneFrameIsInvalid()
    {
        // FF_DL must exceed the 6 data bytes of an 8-byte FF (otherwise an SF would be used).
        Assert.Empty(IsoTpReassembler.Reassemble([Iso.F(Ecu, "10 06 62 F1 90 01 02 03", 0)]));
    }

    [Fact]
    public void FirstFrameLengthSevenNeedsExactlyOneConsecutiveFrameByte()
    {
        var frames = new[]
        {
            Iso.F(Ecu, "10 07 62 F1 90 01 02 03", 0),
            Iso.F(Ecu, "21 04 AA AA AA AA AA AA", 1)
        };
        var message = Single(frames);
        Assert.True(message.IsComplete);
        Assert.Equal(Iso.H("62F19001020304"), message.Payload);
    }

    [Fact]
    public void FirstFrame12BitMaximum4095WithSequenceWrapEveryFifteen()
    {
        // FF_DL = 4095 = 0xFFF: FF 0x1F 0xFF + 6 bytes, then ceil((4095-6)/7) = 585 CFs.
        // SN runs 1..F,0,1..F,0 ... (9.6.4.3); 585 = 39 * 15 -> final SN = 585 mod 16 = 9.
        var pdu = Enumerable.Range(0, 4095).Select(i => (byte)(i * 31 + 7)).ToArray();
        var wire = Iso.SegmentClassic(pdu, null);
        Assert.Equal(0x1F, wire[0][0]);
        Assert.Equal(0xFF, wire[0][1]);
        Assert.Equal(586, wire.Count);
        Assert.Equal(0x29, wire[^1][0]); // 0x20 | (585 & 0xF) = 0x29

        var frames = wire.Select((bytes, i) => new CanFrame(0, Ecu, bytes, i * Iso.Ms)).ToArray();
        var message = Single(frames);
        Assert.True(message.IsComplete);
        Assert.Equal(pdu, message.Payload);
        Assert.Equal(586, message.FrameIndices.Count);
    }

    [Fact]
    public void SequenceNumberWrapsFromFifteenToZeroNotToOne()
    {
        // 6 + 7*16 = 118 bytes: 16 CFs, SN = 1..F then 0 (the 16th CF carries SN 0).
        var pdu = Enumerable.Range(0, 118).Select(i => (byte)i).ToArray();
        var wire = Iso.SegmentClassic(pdu, null);
        Assert.Equal(17, wire.Count);
        Assert.Equal(0x2F, wire[15][0]);
        Assert.Equal(0x20, wire[16][0]);
        var message = Single(wire.Select((b, i) => new CanFrame(0, Ecu, b, i * Iso.Ms)));
        Assert.True(message.IsComplete);
        Assert.Equal(pdu, message.Payload);
    }

    [Fact]
    public void SequenceNumberOneAfterFifteenIsASequenceError()
    {
        // After SN 0xF the next legal SN is 0x0. A sender repeating 0x1 violates 9.6.4.3.
        var pdu = Enumerable.Range(0, 118).Select(i => (byte)i).ToArray();
        var wire = Iso.SegmentClassic(pdu, null);
        wire[16][0] = 0x21; // wrong: wraps to 1 instead of 0
        var message = Single(wire.Select((b, i) => new CanFrame(0, Ecu, b, i * Iso.Ms)));
        Assert.False(message.IsComplete);
        Assert.Contains("sequence", message.TransportIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstFrame32BitEscapeLengthIsDecodedBigEndian()
    {
        // FF_DL escape (9.6.3.2): bytes 10 00, then FF_DL = 0x00001388 = 5000, 2 data bytes in FF.
        // Remaining 4998 bytes => 714 CFs (7 each = 4998 exactly).
        var pdu = Enumerable.Range(0, 5000).Select(i => (byte)(i ^ 0x5A)).ToArray();
        var wire = Iso.SegmentClassic(pdu, null);
        Assert.Equal(Iso.H("10 00 00 00 13 88"), wire[0][..6]);
        Assert.Equal(715, wire.Count);
        var message = Single(wire.Select((b, i) => new CanFrame(0, Ecu, b, i * Iso.Ms)));
        Assert.True(message.IsComplete);
        Assert.Equal(pdu, message.Payload);
    }

    [Fact]
    public void CanFdFirstFrameCarries62BytesAndConsecutiveFrames63()
    {
        // FD: FF header 2 bytes + 62 data; CF header 1 byte + 63 data. 200 = 62 + 63 + 63 + 12.
        var pdu = Enumerable.Range(0, 200).Select(i => (byte)(255 - i)).ToArray();
        var wire = Iso.SegmentFd(pdu);
        Assert.Equal(4, wire.Count);
        Assert.Equal(new byte[] { 0x10, 0xC8 }, wire[0][..2]);
        var frames = wire.Select((b, i) =>
            new CanFrame(0, Ecu, b, i * Iso.Ms, CanFrameFlags.FlexibleDataRate)).ToArray();
        var message = Single(frames);
        Assert.True(message.IsComplete);
        Assert.Equal(pdu, message.Payload);
    }

    // ---------------- Fault handling in the receiver (9.8) ----------------

    [Fact]
    public void MissingConsecutiveFrameGapAbortsTheTransfer()
    {
        // CF with SN 2 never arrives; SN 3 is "wrong SN" => reception aborted (N_WRONG_SN, 9.8.3).
        var frames = new[]
        {
            Iso.F(Ecu, "10 1E 62 F1 90 01 02 03", 0),
            Iso.F(Ecu, "21 04 05 06 07 08 09 0A", 1),
            Iso.F(Ecu, "23 12 13 14 15 16 17 18", 3)
        };
        var message = Single(frames);
        Assert.False(message.IsComplete);
        Assert.Contains("sequence", message.TransportIssue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expected 2", message.TransportIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicatedConsecutiveFrameIsASequenceErrorAndDoesNotDuplicateBytes()
    {
        var frames = new[]
        {
            Iso.F(Ecu, "10 14 62 F1 90 01 02 03", 0),
            Iso.F(Ecu, "21 04 05 06 07 08 09 0A", 1),
            Iso.F(Ecu, "21 04 05 06 07 08 09 0A", 2),
            Iso.F(Ecu, "22 0B 0C 0D 0E 0F 10 11", 3)
        };
        var message = Single(frames);
        Assert.False(message.IsComplete);
        Assert.Contains("sequence", message.TransportIssue, StringComparison.OrdinalIgnoreCase);
        // Only the first copy of CF1 is in the payload: 6 + 7 bytes at most.
        Assert.True(message.Payload.Length <= 13);
    }

    [Fact]
    public void OutOfOrderSequenceNumbersAbortTheTransfer()
    {
        var frames = new[]
        {
            Iso.F(Ecu, "10 1E 62 F1 90 01 02 03", 0),
            Iso.F(Ecu, "21 04 05 06 07 08 09 0A", 1),
            Iso.F(Ecu, "23 12 13 14 15 16 17 18", 2), // 3 before 2
            Iso.F(Ecu, "22 0B 0C 0D 0E 0F 10 11", 3)
        };
        var message = Single(frames);
        Assert.False(message.IsComplete);
        Assert.Contains("sequence", message.TransportIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsecutiveFrameBeforeAnyFirstFrameIsIgnored()
    {
        Assert.Empty(IsoTpReassembler.Reassemble([Iso.F(Ecu, "21 04 05 06 07 08 09 0A", 0)]));
    }

    [Fact]
    public void IncompleteTransferAtEndOfCaptureIsReportedWithByteCounts()
    {
        var frames = new[]
        {
            Iso.F(Ecu, "10 14 62 F1 90 01 02 03", 0),
            Iso.F(Ecu, "21 04 05 06 07 08 09 0A", 1)
        };
        var message = Single(frames);
        Assert.False(message.IsComplete);
        Assert.Contains("13/20", message.TransportIssue);
    }

    [Fact]
    public void ConsecutiveFrameAfterMoreThanFiveSecondsDoesNotContinueTheTransfer()
    {
        var frames = new[]
        {
            Iso.F(Ecu, "10 14 62 F1 90 01 02 03", 0),
            Iso.F(Ecu, "21 04 05 06 07 08 09 0A", 6000),
            Iso.F(Ecu, "22 0B 0C 0D 0E 0F 10 11", 6001)
        };
        var messages = IsoTpReassembler.Reassemble(frames);
        Assert.DoesNotContain(messages, m => m.IsComplete);
        Assert.Contains(messages, m => m.TransportIssue.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConsecutiveFramesWithinTimeoutAreAccepted()
    {
        var frames = new[]
        {
            Iso.F(Ecu, "10 14 62 F1 90 01 02 03", 0),
            Iso.F(Ecu, "21 04 05 06 07 08 09 0A", 900),
            Iso.F(Ecu, "22 0B 0C 0D 0E 0F 10 11", 1800)
        };
        Assert.True(Single(frames).IsComplete);
    }

    // ---------------- Two ECUs interleaved ----------------

    [Fact]
    public void InterleavedMultiFrameTransfersFromTwoEcusAreKeptSeparate()
    {
        const uint ecuA = 0x7E8, ecuB = 0x7E9;
        var frames = new[]
        {
            Iso.F(ecuA, "10 0D 62 F1 90 A1 A2 A3", 0),
            Iso.F(ecuB, "10 0D 62 F1 91 B1 B2 B3", 1),
            Iso.F(ecuB, "21 B4 B5 B6 B7 B8 B9 BA", 2),
            Iso.F(ecuA, "21 A4 A5 A6 A7 A8 A9 AA", 3)
        };
        var messages = IsoTpReassembler.Reassemble(frames);
        Assert.Equal(2, messages.Count);
        var a = Assert.Single(messages, m => m.Identifier == ecuA);
        var b = Assert.Single(messages, m => m.Identifier == ecuB);
        Assert.Equal(Iso.H("62F190A1A2A3A4A5A6A7A8A9AA"), a.Payload);
        Assert.Equal(Iso.H("62F191B1B2B3B4B5B6B7B8B9BA"), b.Payload);
        Assert.Equal(new[] { 0, 3 }, a.FrameIndices);
        Assert.Equal(new[] { 1, 2 }, b.FrameIndices);
        Assert.All(messages, m => Assert.True(m.IsComplete));
    }

    [Fact]
    public void SameIdentifierOnTwoBusChannelsNeverMixes()
    {
        var frames = new[]
        {
            Iso.F(Ecu, "10 0D 62 F1 90 A1 A2 A3", 0, channel: 0),
            Iso.F(Ecu, "21 B4 B5 B6 B7 B8 B9 BA", 1, channel: 1) // CF on another bus: orphan
        };
        var message = Single(frames);
        Assert.False(message.IsComplete);
    }

    // ---------------- Addressing (clause 10) ----------------

    [Fact]
    public void ExtendedAddressingSkipsTheTargetAddressByteBeforeTheNPci()
    {
        // Normal-extended addressing (10.3.3): data byte 0 = N_TA, N_PCI starts at byte 1.
        // SF carries at most 6 bytes. N_TA = 0xF1 (tester).
        var message = Single_ExtAddr([new CanFrame(0, 0x7E8, Iso.H("F1 05 62 F1 90 41 42"), 0)]);
        Assert.Equal(Iso.H("62F1904142"), message.Payload);
    }

    [Fact]
    public void MixedAddressingSkipsTheAddressExtensionByteAndReassemblesMultiFrame()
    {
        // Mixed addressing (10.3.4): data byte 0 = N_AE; payload per frame is one byte shorter.
        // FF_DL = 10: FF carries 5 bytes, CF1 carries 5 more (6 usable -> 5 here).
        var frames = new[]
        {
            new CanFrame(0, 0x18CEF100u, Iso.H("A5 10 0A 62 F1 90 01 02"), 0, CanFrameFlags.Extended),
            new CanFrame(0, 0x18CEF100u, Iso.H("A5 21 03 04 05 06 07 AA"), Iso.Ms, CanFrameFlags.Extended)
        };
        var message = Assert.Single(IsoTpReassembler.Reassemble(frames, addressingPrefixBytes: 1));
        Assert.True(message.IsComplete);
        Assert.Equal(Iso.H("62F1900102 0304050607").Take(10).ToArray(), message.Payload);
    }

    private static IsoTpMessage Single_ExtAddr(CanFrame[] frames) =>
        Assert.Single(IsoTpReassembler.Reassemble(frames, addressingPrefixBytes: 1));

    // ---------------- STmin / FC encodings (9.6.5.4) ----------------

    [Theory]
    [InlineData(0x00, 0.0)]
    [InlineData(0x01, 1.0)]
    [InlineData(0x14, 20.0)]
    [InlineData(0x7F, 127.0)]
    [InlineData(0xF1, 0.1)]
    [InlineData(0xF2, 0.2)]
    [InlineData(0xF5, 0.5)]
    [InlineData(0xF9, 0.9)]
    // Reserved values 0x80-0xF0 and 0xFA-0xFF: the sender shall use the longest STmin, 127 ms.
    [InlineData(0x80, 127.0)]
    [InlineData(0xF0, 127.0)]
    [InlineData(0xFA, 127.0)]
    [InlineData(0xFF, 127.0)]
    public void StMinEncodingFollowsIso15765Table(byte raw, double expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, DiagnosticTimingAnalyzer.DecodeStMinMilliseconds(raw), 6);
    }

    private static IReadOnlyList<DiagnosticTimingFinding> TimingFor(
        IReadOnlyList<CanFrame> frames, DiagnosticTimingOptions? options = null)
    {
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        return DiagnosticTimingAnalyzer.Analyze(transactions, frames, options).Findings;
    }

    /// <summary>Request (SF) from the tester, FF from the ECU, then the given FC / CF script.</summary>
    private static List<CanFrame> ResponseScript(params (uint Id, string Hex, long TNs)[] script)
    {
        var frames = new List<CanFrame> { Iso.F(Tester, "03 22 F1 90 AA AA AA AA", 0) };
        frames.AddRange(script.Select(s => Iso.Fns(s.Id, s.Hex, s.TNs)));
        return frames;
    }

    [Fact]
    public void ConsecutiveFramesFasterThanRequestedStMinMillisecondsAreFlagged()
    {
        // FC CTS, BS 0, STmin 0x14 = 20 ms. CFs 5 ms apart violate it.
        var frames = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "30 00 14 AA AA AA AA AA", 12 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 32 * Iso.Ms),
            (Ecu, "22 0B 0C 0D AA AA AA AA", 37 * Iso.Ms));
        Assert.Contains(TimingFor(frames), f => f.RuleId == "TIM-009");
    }

    [Fact]
    public void ConsecutiveFramesHonouringStMinAreNotFlagged()
    {
        var frames = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "30 00 14 AA AA AA AA AA", 12 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 32 * Iso.Ms),
            (Ecu, "22 0B 0C 0D AA AA AA AA", 53 * Iso.Ms));
        Assert.DoesNotContain(TimingFor(frames), f => f.RuleId == "TIM-009");
    }

    [Fact]
    public void MicrosecondStMinCodeF5MeansHalfAMillisecond()
    {
        // 0xF5 = 500 us. CF gap of 100 us violates (shortfall far beyond the 10% + 0.1 ms tolerance);
        // CF gap of 600 us conforms.
        var violating = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "30 00 F5 AA AA AA AA AA", 11 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 12 * Iso.Ms),
            (Ecu, "22 0B 0C 0D AA AA AA AA", 12 * Iso.Ms + 100_000));
        var conforming = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "30 00 F5 AA AA AA AA AA", 11 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 12 * Iso.Ms),
            (Ecu, "22 0B 0C 0D AA AA AA AA", 12 * Iso.Ms + 600_000));
        Assert.Contains(TimingFor(violating), f => f.RuleId == "TIM-009");
        Assert.DoesNotContain(TimingFor(conforming), f => f.RuleId == "TIM-009");
    }

    [Fact]
    public void FlowControlArrivingAfterNBsOfOneSecondIsFlagged()
    {
        // N_Bs timeout = 1000 ms (ISO 15765-2 Table 17): FC 1500 ms after FF.
        var frames = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "30 00 00 AA AA AA AA AA", 1510 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 1511 * Iso.Ms),
            (Ecu, "22 0B 0C 0D AA AA AA AA", 1512 * Iso.Ms));
        Assert.Contains(TimingFor(frames), f => f.RuleId == "TIM-007");
    }

    [Fact]
    public void FlowControlWithinNBsIsNotFlagged()
    {
        var frames = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "30 00 00 AA AA AA AA AA", 900 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 901 * Iso.Ms),
            (Ecu, "22 0B 0C 0D AA AA AA AA", 902 * Iso.Ms));
        Assert.DoesNotContain(TimingFor(frames), f => f.RuleId == "TIM-007");
    }

    [Fact]
    public void WaitFlowControlRestartsNBsAndCtsAfterOneSecondIsFlagged()
    {
        // FS = 1 (WAIT) restarts N_Bs; CTS 1400 ms after the WAIT exceeds 1000 ms.
        var frames = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "31 00 00 AA AA AA AA AA", 100 * Iso.Ms),
            (Tester, "30 00 00 AA AA AA AA AA", 1500 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 1501 * Iso.Ms),
            (Ecu, "22 0B 0C 0D AA AA AA AA", 1502 * Iso.Ms));
        Assert.Contains(TimingFor(frames), f => f.RuleId == "TIM-007");
    }

    [Fact]
    public void WaitFlowControlFollowedByTimelyCtsIsNotFlagged()
    {
        var frames = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "31 00 00 AA AA AA AA AA", 100 * Iso.Ms),
            (Tester, "30 00 00 AA AA AA AA AA", 900 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 901 * Iso.Ms),
            (Ecu, "22 0B 0C 0D AA AA AA AA", 902 * Iso.Ms));
        Assert.DoesNotContain(TimingFor(frames), f => f.RuleId == "TIM-007");
    }

    [Fact]
    public void OverflowFlowControlStatusTwoIsReported()
    {
        var frames = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "32 00 00 AA AA AA AA AA", 12 * Iso.Ms));
        Assert.Contains(TimingFor(frames), f => f.RuleId == "TIM-010");
    }

    [Fact]
    public void ConsecutiveFrameGapOverNCrOfOneSecondIsFlaggedEvenWithinReassemblerTimeout()
    {
        // N_Cr = 1000 ms: CF 1500 ms after FC.
        var frames = ResponseScript(
            (Ecu, "10 0E 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "30 00 00 AA AA AA AA AA", 12 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 1512 * Iso.Ms),
            (Ecu, "22 0B 0C 0D AA AA AA AA", 1513 * Iso.Ms));
        Assert.Contains(TimingFor(frames), f => f.RuleId == "TIM-008");
    }

    [Fact]
    public void BlockSizeTwoRequiresAnotherFlowControlAfterTwoConsecutiveFrames()
    {
        // BS = 2: after CF1,CF2 the sender must wait for a new FC. With the new FC present and
        // all gaps inside limits, nothing is flagged; the whole PDU still reassembles.
        // FF_DL = 0x1B = 27 = 6 + 3*7.
        var frames = ResponseScript(
            (Ecu, "10 1B 62 F1 90 01 02 03", 10 * Iso.Ms),
            (Tester, "30 02 00 AA AA AA AA AA", 11 * Iso.Ms),
            (Ecu, "21 04 05 06 07 08 09 0A", 12 * Iso.Ms),
            (Ecu, "22 0B 0C 0D 0E 0F 10 11", 13 * Iso.Ms),
            (Tester, "30 02 00 AA AA AA AA AA", 14 * Iso.Ms),
            (Ecu, "23 12 13 14 15 16 17 18", 15 * Iso.Ms));
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(27, transaction.Responses[0].Payload.Length);
        Assert.Empty(DiagnosticTimingAnalyzer.Analyze(
            UdsTransactionAnalyzer.Analyze(frames), frames).Findings
            .Where(f => f.RuleId is "TIM-007" or "TIM-008" or "TIM-009"));
    }
}

public sealed class UdsServiceIndependentConformanceTests
{
    private const uint Tester = 0x7E0;
    private const uint Ecu = 0x7E8;

    private static DiagnosticTransaction Run(string requestHex, string responseHex,
        long requestMs = 0, long responseMs = 5, byte? pad = 0xAA)
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, requestHex, requestMs, pad: pad);
        Iso.Send(frames, Ecu, responseHex, responseMs, pad: pad);
        return Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
    }

    // ---------------- Positive responses: SID + 0x40 (ISO 14229-1 8.7) ----------------

    public static TheoryData<byte, string, string, string> PositiveVectors => new()
    {
        { 0x10, "10 03", "50 03 00 32 01 F4", "DiagnosticSessionControl" },
        { 0x11, "11 01", "51 01", "EcuReset" },
        { 0x14, "14 FF FF FF", "54", "ClearDiagnosticInformation" },
        { 0x19, "19 01 09", "59 01 FF 01 00 02", "ReadDTCInformation" },
        { 0x19, "19 02 08", "59 02 FF 12 34 56 29", "ReadDTCInformation" },
        { 0x19, "19 0A", "59 0A FF 12 34 56 08", "ReadDTCInformation" },
        { 0x22, "22 F1 90", "62 F1 90 41 42 43", "ReadDataByIdentifier" },
        { 0x23, "23 22 08 00 00 02", "63 AA BB", "ReadMemoryByAddress" },
        { 0x24, "24 F1 90", "64 F1 90 01", "ReadScalingDataByIdentifier" },
        { 0x27, "27 01", "67 01 11 22 33 44", "SecurityAccess" },
        { 0x27, "27 02 AA BB CC DD", "67 02", "SecurityAccess" },
        { 0x28, "28 00 01", "68 00", "CommunicationControl" },
        { 0x2A, "2A 04 E4", "6A", "ReadDataByPeriodicIdentifier" },
        { 0x2C, "2C 03 F2 00", "6C 03 F2 00", "DynamicallyDefineDataIdentifier" },
        { 0x2E, "2E F1 90 41 42", "6E F1 90", "WriteDataByIdentifier" },
        { 0x2F, "2F F1 90 03 FF", "6F F1 90 03 FF", "InputOutputControlByIdentifier" },
        { 0x31, "31 01 FF 00", "71 01 FF 00", "RoutineControl" },
        { 0x34, "34 00 22 08 00 00 10", "74 20 04 00", "RequestDownload" },
        { 0x35, "35 00 22 08 00 00 10", "75 20 04 00", "RequestUpload" },
        { 0x36, "36 01 AA BB CC", "76 01", "TransferData" },
        { 0x37, "37", "77", "RequestTransferExit" },
        { 0x3E, "3E 00", "7E 00", "TesterPresent" },
        { 0x85, "85 01", "C5 01", "ControlDTCSetting" },
    };

    [Theory]
    [MemberData(nameof(PositiveVectors))]
    public void PositiveResponseCarriesRequestSidPlus0x40AndPairsWithItsRequest(
        byte sid, string request, string response, string name)
    {
        var transaction = Run(request, response);
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(sid, transaction.Decoded.ServiceId);
        Assert.Equal(name, transaction.Decoded.ServiceName);
        Assert.Equal(Iso.H(request), transaction.Request!.Payload);
        var positive = Assert.Single(transaction.Responses);
        Assert.Equal((byte)(sid + 0x40), positive.Payload[0]);
        Assert.Equal(Iso.H(response), positive.Payload);
        Assert.Null(transaction.Decoded.NegativeResponseCode);
        // ISO 15765-4: tester 0x7E0 <-> ECU 0x7E8 is ECU number 0.
        Assert.Equal("ECU 0", transaction.Endpoint.Ecu);
        // Request SF at t=0 ms, response SF at t=5 ms: 5 ms between request end and response start.
        Assert.Equal(5.0, transaction.ResponseTimeMilliseconds!.Value, 6);
    }

    [Fact]
    public void ResponseWithWrongSidOffsetIsNotPairedToTheRequest()
    {
        // 0x62 answers 0x22; 0x61 / 0x63 must not.
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "22 F1 90", 0);
        Iso.Send(frames, Ecu, "63 F1 90 41", 5);
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.DoesNotContain(transactions, t => t.Outcome == DiagnosticOutcome.Positive &&
            t.Decoded.ServiceId == 0x22);
    }

    [Fact]
    public void SessionControlResponseAnnouncesP2AndP2StarWithTheirDifferentResolutions()
    {
        // ISO 14229-1 9.2.? : P2Server_max = 0x0032 -> 50 ms (1 ms units);
        // P2*Server_max = 0x01F4 -> 500 * 10 ms = 5000 ms.
        var transaction = Run("10 03", "50 03 00 32 01 F4");
        Assert.Contains("extended session", transaction.Decoded.ObjectText + transaction.Decoded.ResponseSummary);
        Assert.Contains("P2 50 ms", transaction.Decoded.ResponseSummary);
        Assert.Contains("P2* 5000 ms", transaction.Decoded.ResponseSummary);
    }

    [Theory]
    [InlineData("10 01", "default session")]
    [InlineData("10 02", "programming session")]
    [InlineData("10 03", "extended session")]
    [InlineData("10 04", "safety-system session")]
    public void DiagnosticSessionTypesFollowIso14229Table(string request, string expected)
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, request, 0);
        var sub = request[^2..];
        Iso.Send(frames, Ecu, "50 " + sub + " 00 32 01 F4", 5);
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Contains(expected, transaction.Decoded.RequestSummary);
        Assert.Contains(expected, transaction.Decoded.ResponseSummary);
    }

    [Fact]
    public void ReadDtcNumberOfDtcByStatusMaskDecodesAvailabilityMaskAndCount()
    {
        // 59 01 <StatusAvailabilityMask FF> <DTCFormatIdentifier 01> <DTCCount 0x0002>
        var transaction = Run("19 01 09", "59 01 FF 01 00 02");
        Assert.Contains("2 DTC", transaction.Decoded.ResponseSummary);
        Assert.Contains("0xFF", transaction.Decoded.ResponseSummary);
    }

    [Fact]
    public void ReadDtcByStatusMaskDecodesThreeByteDtcAndStatusByte()
    {
        var transaction = Run("19 02 08", "59 02 FF 12 34 56 29");
        var dtc = Assert.Single(transaction.Decoded.Dtcs);
        Assert.Equal(0x123456, dtc.Code);
        Assert.Equal((byte)0x29, dtc.Status);
    }

    [Fact]
    public void ReadDtcByStatusMaskDecodesSeveralDtcRecords()
    {
        // Two DTCAndStatusRecords of 4 bytes: 0x010203/0x01 and 0xC00044/0x08. 2+1+8 = 11 bytes -> FF/CF.
        var transaction = Run("19 02 FF", "59 02 FF 01 02 03 01 C0 00 44 08");
        Assert.Equal(2, transaction.Decoded.Dtcs.Count);
        Assert.Equal(0x010203, transaction.Decoded.Dtcs[0].Code);
        Assert.Equal((byte)0x01, transaction.Decoded.Dtcs[0].Status);
        Assert.Equal(0xC00044, transaction.Decoded.Dtcs[1].Code);
        Assert.Equal((byte)0x08, transaction.Decoded.Dtcs[1].Status);
    }

    [Fact]
    public void ReadDtcSupportedDtcSubFunction0AUsesTheSameRecordLayout()
    {
        var transaction = Run("19 0A", "59 0A FF 12 34 56 08");
        var dtc = Assert.Single(transaction.Decoded.Dtcs);
        Assert.Equal(0x123456, dtc.Code);
        Assert.Equal((byte)0x08, dtc.Status);
    }

    [Fact]
    public void ReadDtcSnapshotByDtcNumberWithNoStoredRecordReturnsOnlyTheDtcAndStatus()
    {
        // ISO 14229-1 11.3.2: when no snapshot is stored the response holds only the
        // DTCAndStatusRecord (3 byte DTC + status) after the sub-function: 59 04 DTC(3) status.
        var transaction = Run("19 04 12 34 56 01", "59 04 12 34 56 29");
        var dtc = Assert.Single(transaction.Decoded.Dtcs);
        Assert.Equal(0x123456, dtc.Code);
        Assert.Equal((byte)0x29, dtc.Status);
    }

    [Fact]
    public void ReadDtcExtendedDataByDtcNumberDecodesDtcStatusAndPairsMultiFrame()
    {
        // 59 06 DTC(3) status(1) ExtDataRecordNumber(1) data(1) = 8 bytes -> ISO-TP FF + CF.
        var transaction = Run("19 06 12 34 56 01", "59 06 12 34 56 29 01 05");
        var dtc = Assert.Single(transaction.Decoded.Dtcs);
        Assert.Equal(0x123456, dtc.Code);
        Assert.Equal((byte)0x29, dtc.Status);
        Assert.Equal(2, transaction.Responses[0].FrameIndices.Count);
    }

    [Theory]
    [InlineData(0, @"^testFailed$", 0x01)]
    [InlineData(1, @"^testFailedThis(Operation)?Cycle$", 0x02)]
    [InlineData(2, @"^pending(DTC)?$", 0x04)]
    [InlineData(3, @"^confirmed(DTC)?$", 0x08)]
    [InlineData(4, @"^(test)?NotCompletedSince(Last)?Clear$", 0x10)]
    [InlineData(5, @"^(test)?FailedSince(Last)?Clear$", 0x20)]
    [InlineData(6, @"^(test)?NotCompletedThis(Operation)?Cycle$", 0x40)]
    [InlineData(7, @"^warningIndicator(Requested)?$", 0x80)]
    public void DtcStatusByteBitMeaningsFollowIso14229Annex_D(int bit, string pattern, int mask)
    {
        // ISO 14229-1 Annex D.2 Table D.2:
        // b0 testFailed, b1 testFailedThisOperationCycle, b2 pendingDTC, b3 confirmedDTC,
        // b4 testNotCompletedSinceLastClear, b5 testFailedSinceLastClear,
        // b6 testNotCompletedThisOperationCycle, b7 warningIndicatorRequested.
        Assert.Equal(1 << bit, mask);
        var transaction = Run("19 02 FF", $"59 02 FF 12 34 56 {mask:X2}");
        var dtc = Assert.Single(transaction.Decoded.Dtcs);
        Assert.Equal((byte)mask, dtc.Status);
        var names = dtc.StatusText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var only = Assert.Single(names);
        Assert.Matches(new System.Text.RegularExpressions.Regex(pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase), only);
    }

    [Fact]
    public void DtcStatusByteWithEveryBitSetNamesAllEightConditions()
    {
        var transaction = Run("19 02 FF", "59 02 FF 12 34 56 FF");
        var names = Assert.Single(transaction.Decoded.Dtcs).StatusText
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DtcStatusByteZeroHasNoActiveCondition()
    {
        var transaction = Run("19 02 FF", "59 02 FF 12 34 56 00");
        var text = Assert.Single(transaction.Decoded.Dtcs).StatusText;
        Assert.Matches("(?i)no status", text);
    }

    [Fact]
    public void ReadDataByIdentifierRequestObjectNamesTheDid()
    {
        var transaction = Run("22 F1 90", "62 F1 90 41 42 43");
        Assert.Contains("F190", transaction.Decoded.ObjectText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadMemoryByAddressUsesAddressAndLengthFormatNibblesInIsoOrder()
    {
        // addressAndLengthFormatIdentifier 0x22: high nibble = memorySize length (2), low = memoryAddress length (2).
        var transaction = Run("23 22 08 00 00 02", "63 AA BB");
        Assert.Contains("0x0800", transaction.Decoded.RequestSummary);
        Assert.Contains("size 2", transaction.Decoded.RequestSummary);
    }

    [Fact]
    public void ReadMemoryWithAsymmetricFormatIdentifierTakesAddressFromLowNibble()
    {
        // 0x14: size length 1 (high nibble), address length 4 (low nibble): address 0x20001000, size 0x10.
        var transaction = Run("23 14 20 00 10 00 10", "63 " + string.Join(' ', Enumerable.Repeat("00", 16)));
        Assert.Contains("0x20001000", transaction.Decoded.RequestSummary);
        Assert.Contains("size 16", transaction.Decoded.RequestSummary);
    }

    [Fact]
    public void RequestDownloadDecodesAddressSizeAndLengthFormat()
    {
        var transaction = Run("34 00 22 08 00 00 10", "74 20 04 00");
        Assert.Contains("0x0800", transaction.Decoded.RequestSummary);
        Assert.Contains("size 16", transaction.Decoded.RequestSummary);
    }

    [Fact]
    public void SecurityAccessOddSubFunctionRequestsSeedEvenSendsKey()
    {
        // ISO 14229-1 10.4: requestSeed = odd (0x01,0x03,...), sendKey = even (0x02,0x04,...).
        Assert.Contains("seed", Run("27 01", "67 01 11 22 33 44").Decoded.RequestSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seed", Run("27 03", "67 03 11 22 33 44").Decoded.RequestSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", Run("27 02 AA BB CC DD", "67 02").Decoded.RequestSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", Run("27 04 AA BB CC DD", "67 04").Decoded.RequestSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("31 01 FF 00", "71 01 FF 00", "Start")]
    [InlineData("31 02 FF 00", "71 02 FF 00", "Stop")]
    [InlineData("31 03 FF 00", "71 03 FF 00", "results")]
    public void RoutineControlTypesFollowIso14229Table(string request, string response, string word)
    {
        // 0x01 startRoutine, 0x02 stopRoutine, 0x03 requestRoutineResults.
        var transaction = Run(request, response);
        Assert.Contains(word, transaction.Decoded.RequestSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransferDataBlockSequenceCounterIsPairedAndWrapsFromFfToZero()
    {
        // blockSequenceCounter is an 8-bit counter (ISO 14229-1 14.5.2) wrapping 0xFF -> 0x00;
        // it is not a sub-function, so bit 7 must not be treated as suppressPosRspMsgIndicationBit.
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "36 FF AA BB", 0);
        Iso.Send(frames, Ecu, "76 FF", 5);
        Iso.Send(frames, Tester, "36 00 CC DD", 10);
        Iso.Send(frames, Ecu, "76 00", 15);
        Iso.Send(frames, Tester, "36 80 EE", 20);
        Iso.Send(frames, Ecu, "76 80", 25);
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(3, transactions.Count);
        Assert.All(transactions, t => Assert.Equal(DiagnosticOutcome.Positive, t.Outcome));
    }

    [Fact]
    public void MultiFrameRequestAndMultiFrameResponsePairWithTimingFromRequestEnd()
    {
        // Request 2E F1 90 + 5 bytes (8 bytes -> FF + CF): FF at 0, CF at 2. Response (10 bytes -> FF + CF)
        // starts at 14. Duration = response start - request END = 14 - 2 = 12 ms.
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "2E F1 90 01 02 03 04 05", 0, stepMs: 2);
        // 8 bytes = 2 frames: FF @0, CF @2
        Iso.Send(frames, Ecu, "6E F1 90", 14);
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(2, transaction.Request!.FrameIndices.Count);
        Assert.Equal(2 * Iso.Ms, transaction.Request.EndTimestampNanoseconds);
        Assert.Equal(12.0, transaction.ResponseTimeMilliseconds!.Value, 6);
        Assert.Equal(0, transaction.StartTimestampNanoseconds);
        Assert.Equal(14 * Iso.Ms, transaction.EndTimestampNanoseconds);
    }

    // ---------------- Negative responses (ISO 14229-1 Annex A) ----------------

    public static TheoryData<byte, string> NrcVectors
    {
        get
        {
            var data = new TheoryData<byte, string>();
            foreach (var (code, name) in new (byte, string)[]
            {
                (0x10, "generalReject"), (0x11, "serviceNotSupported"),
                (0x12, "subFunctionNotSupported"), (0x13, "incorrectMessageLengthOrInvalidFormat"),
                (0x14, "responseTooLong"), (0x21, "busyRepeatRequest"), (0x22, "conditionsNotCorrect"),
                (0x24, "requestSequenceError"), (0x25, "noResponseFromSubnetComponent"),
                (0x26, "failurePreventsExecutionOfRequestedAction"), (0x31, "requestOutOfRange"),
                (0x33, "securityAccessDenied"), (0x34, "authenticationRequired"), (0x35, "invalidKey"),
                (0x36, "exceedNumberOfAttempts"), (0x37, "requiredTimeDelayNotExpired"),
                (0x70, "uploadDownloadNotAccepted"), (0x71, "transferDataSuspended"),
                (0x72, "generalProgrammingFailure"), (0x73, "wrongBlockSequenceCounter"),
                (0x7E, "subFunctionNotSupportedInActiveSession"),
                (0x7F, "serviceNotSupportedInActiveSession")
            })
                data.Add(code, name);
            return data;
        }
    }

    private static string Canonical(string name) =>
        name.Replace("exceededNumberOfAttempts", "exceedNumberOfAttempts");

    [Theory]
    [MemberData(nameof(NrcVectors))]
    public void NegativeResponseIsPairedAndNrcNamedPerAnnexA_ReadDataByIdentifier(byte nrc, string name)
    {
        var transaction = Run("22 F1 90", $"7F 22 {nrc:X2}");
        AssertNegative(transaction, nrc, name);
    }

    [Theory]
    [MemberData(nameof(NrcVectors))]
    public void NegativeResponseIsPairedAndNrcNamedPerAnnexA_RoutineControl(byte nrc, string name)
    {
        var transaction = Run("31 01 FF 00", $"7F 31 {nrc:X2}");
        AssertNegative(transaction, nrc, name);
    }

    private static void AssertNegative(DiagnosticTransaction transaction, byte nrc, string name)
    {
        Assert.Equal(DiagnosticOutcome.Negative, transaction.Outcome);
        Assert.Equal(nrc, transaction.Decoded.NegativeResponseCode);
        // Annex A spells 0x36 "exceededNumberOfAttempts" in later editions and
        // "exceedNumberOfAttempts" in earlier ones: accept either, nothing else.
        Assert.Equal(Canonical(name), Canonical(transaction.Decoded.NegativeResponseText));
        Assert.Equal(new byte[] { 0x7F, transaction.Request!.Payload[0], nrc }, transaction.Responses[0].Payload);
        Assert.Contains(name[..8], transaction.Decoded.ResponseSummary);
    }

    [Fact]
    public void UdsServiceDecoderNamesEveryAnnexANrcAndLabelsReservedRanges()
    {
        Assert.True(UdsServiceDecoder.IsStandardNrc(0x78));
        Assert.Equal("responsePending", UdsServiceDecoder.NrcName(0x78));
        Assert.StartsWith("reservedForSpecificConditionsNotCorrect", UdsServiceDecoder.NrcName(0x95));
        Assert.StartsWith("vehicleManufacturerSpecificConditionsNotCorrect", UdsServiceDecoder.NrcName(0xF0));
        Assert.False(UdsServiceDecoder.IsStandardNrc(0x23));
        Assert.False(UdsServiceDecoder.IsStandardNrc(0x15));
    }

    // ---------------- Response pending (NRC 0x78) ----------------

    [Fact]
    public void ResponsePendingThenFinalPositiveResponseIsOneTransaction()
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "22 F1 90", 0);
        Iso.Send(frames, Ecu, "7F 22 78", 10);
        Iso.Send(frames, Ecu, "7F 22 78", 2010);
        Iso.Send(frames, Ecu, "62 F1 90 41", 3500);
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(3, transaction.Responses.Count);
        Assert.Equal(Iso.H("7F2278"), transaction.Responses[0].Payload);
        Assert.Equal(Iso.H("62F19041"), transaction.Responses[2].Payload);
        Assert.Null(transaction.Decoded.NegativeResponseCode);
        // Overall request -> final response time: 3500 ms - 0.
        Assert.Equal(3500.0, transaction.ResponseTimeMilliseconds!.Value, 6);
        Assert.Equal(3500 * Iso.Ms, transaction.EndTimestampNanoseconds);
    }

    [Fact]
    public void ResponsePendingThenFinalNegativeResponseKeepsTheFinalNrc()
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "31 01 FF 00", 0);
        Iso.Send(frames, Ecu, "7F 31 78", 10);
        Iso.Send(frames, Ecu, "7F 31 72", 4000);
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Negative, transaction.Outcome);
        Assert.Equal((byte)0x72, transaction.Decoded.NegativeResponseCode);
        Assert.Equal(2, transaction.Responses.Count);
    }

    [Fact]
    public void ResponsePendingWithoutFinalResponseIsNotACompletedTransactionAndIsFlagged()
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "31 01 FF 00", 0);
        Iso.Send(frames, Ecu, "7F 31 78", 10);
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        var transaction = Assert.Single(transactions);
        Assert.Equal(DiagnosticOutcome.NoResponse, transaction.Outcome);
        Assert.Null(transaction.Decoded.NegativeResponseCode);
        var report = DiagnosticTimingAnalyzer.Analyze(transactions, frames);
        Assert.Contains(report.Findings, f => f.RuleId == "TIM-004");
    }

    [Fact]
    public void ResponsePendingIsHonouredEvenWhenTheRequestSuppressedThePositiveResponse()
    {
        // ISO 14229-1 8.2.2/8.8: suppressPosRspMsgIndicationBit never suppresses NRC 0x78,
        // and once 0x78 was sent the ECU shall also send the final response.
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "10 83", 0);
        Iso.Send(frames, Ecu, "7F 10 78", 10);
        Iso.Send(frames, Ecu, "50 03 00 32 01 F4", 300);
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(2, transaction.Responses.Count);
    }

    // ---------------- suppressPosRspMsgIndicationBit (8.2.2) ----------------

    [Theory]
    [InlineData("10 83")]
    [InlineData("11 81")]
    [InlineData("19 82 08")]
    [InlineData("27 82 AA BB")]
    [InlineData("28 80 01")]
    [InlineData("2C 83 F2 00")]
    [InlineData("31 81 FF 00")]
    [InlineData("3E 80")]
    [InlineData("85 81")]
    public void SuppressBitOnSubFunctionMeansNoPositiveResponseIsExpected(string request)
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, request, 0);
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.SuppressedResponse, transaction.Outcome);
    }

    [Theory]
    [InlineData("10 03")]
    [InlineData("11 01")]
    [InlineData("19 02 08")]
    [InlineData("27 02 AA BB")]
    [InlineData("28 00 01")]
    [InlineData("2C 03 F2 00")]
    [InlineData("31 01 FF 00")]
    [InlineData("3E 00")]
    [InlineData("85 01")]
    public void SubFunctionWithoutSuppressBitWithoutAnswerIsNoResponse(string request)
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, request, 0);
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.NoResponse, transaction.Outcome);
    }

    [Theory]
    [InlineData("22 80 10")]          // DID 0x8010
    [InlineData("2E 80 10 01")]       // DID 0x8010
    [InlineData("2F 80 10 03")]       // DID 0x8010
    [InlineData("36 80 AA")]          // blockSequenceCounter 0x80
    [InlineData("34 80 22 08 00 00 10")] // dataFormatIdentifier 0x80
    public void ByteWithBit7OnServicesWithoutSubFunctionIsNotASuppressBit(string request)
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, request, 0);
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.NoResponse, transaction.Outcome);
    }

    [Fact]
    public void NegativeResponseIsAlwaysSentEvenWhenPositiveResponseWasSuppressed()
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "10 83", 0);
        Iso.Send(frames, Ecu, "7F 10 22", 5);
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Negative, transaction.Outcome);
        Assert.Equal((byte)0x22, transaction.Decoded.NegativeResponseCode);
    }

    [Fact]
    public void PositiveResponseEchoesSubFunctionWithoutTheSuppressBit()
    {
        // 14229-1 8.2.2: the response echoes the sub-function value with bit 7 cleared.
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "3E 80", 0);                // suppressed request
        Iso.Send(frames, Tester, "3E 00", 100);              // ordinary request
        Iso.Send(frames, Ecu, "7E 00", 105);
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(2, transactions.Count);
        Assert.Equal(DiagnosticOutcome.SuppressedResponse, transactions[0].Outcome);
        Assert.Equal(DiagnosticOutcome.Positive, transactions[1].Outcome);
        Assert.Equal(Iso.H("3E00"), transactions[1].Request!.Payload);
    }

    // ---------------- Correlation / addressing ----------------

    [Fact]
    public void OutstandingReadsOfTwoDidsToOneEcuPairByDidEvenWhenAnsweredInReverse()
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "22 F1 90", 0);
        Iso.Send(frames, Tester, "22 F1 91", 1);
        Iso.Send(frames, Ecu, "62 F1 91 BB", 5);
        Iso.Send(frames, Ecu, "62 F1 90 AA", 6);
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(2, transactions.Count);
        Assert.All(transactions, t => Assert.Equal(DiagnosticOutcome.Positive, t.Outcome));
        var first = transactions.Single(t => t.Request!.Payload[2] == 0x90);
        Assert.Equal(Iso.H("62F190AA"), first.Responses[0].Payload);
        Assert.Equal(6.0 - 0.0, first.ResponseTimeMilliseconds!.Value, 6);
        var second = transactions.Single(t => t.Request!.Payload[2] == 0x91);
        Assert.Equal(Iso.H("62F191BB"), second.Responses[0].Payload);
        Assert.Equal(4.0, second.ResponseTimeMilliseconds!.Value, 6);
    }

    [Fact]
    public void InterleavedMultiFrameAnswersFromTwoEcusYieldTwoCorrectTransactions()
    {
        // ECU0 = 0x7E0/0x7E8, ECU1 = 0x7E1/0x7E9 (ISO 15765-4). Both read DID F190, answers interleave.
        var frames = new List<CanFrame>
        {
            Iso.F(0x7E0, "03 22 F1 90 AA AA AA AA", 0),
            Iso.F(0x7E1, "03 22 F1 90 AA AA AA AA", 1),
            Iso.F(0x7E8, "10 0D 62 F1 90 A1 A2 A3", 5),
            Iso.F(0x7E9, "10 0D 62 F1 90 B1 B2 B3", 6),
            Iso.F(0x7E0, "30 00 00 AA AA AA AA AA", 7),
            Iso.F(0x7E1, "30 00 00 AA AA AA AA AA", 8),
            Iso.F(0x7E9, "21 B4 B5 B6 B7 B8 B9 BA", 9),
            Iso.F(0x7E8, "21 A4 A5 A6 A7 A8 A9 AA", 10)
        };
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(2, transactions.Count);
        var ecu0 = transactions.Single(t => t.Request!.Identifier == 0x7E0);
        var ecu1 = transactions.Single(t => t.Request!.Identifier == 0x7E1);
        Assert.Equal("ECU 0", ecu0.Endpoint.Ecu);
        Assert.Equal("ECU 1", ecu1.Endpoint.Ecu);
        Assert.Equal(0x7E8u, ecu0.Responses[0].Identifier);
        Assert.Equal(0x7E9u, ecu1.Responses[0].Identifier);
        Assert.Equal(0xA1, ecu0.Responses[0].Payload[3]);
        Assert.Equal(0xB1, ecu1.Responses[0].Payload[3]);
        Assert.Equal(13, ecu0.Responses[0].Payload.Length);
        // ECU0 response FF @5 ms - request SF @0 ms.
        Assert.Equal(5.0, ecu0.ResponseTimeMilliseconds!.Value, 6);
        Assert.Equal(5.0, ecu1.ResponseTimeMilliseconds!.Value, 6); // 6 - 1
        // Own frames + the FC candidate sent to that ECU's FF; never the other ECU's frames.
        Assert.DoesNotContain(1, ecu0.FrameIndices);
        Assert.DoesNotContain(3, ecu0.FrameIndices);
        Assert.DoesNotContain(6, ecu0.FrameIndices);
        Assert.DoesNotContain(0, ecu1.FrameIndices);
        Assert.DoesNotContain(2, ecu1.FrameIndices);
    }

    [Fact]
    public void ResponseFromTheWrongPhysicalIdentifierIsNotPairedToTheRequest()
    {
        // ISO 15765-4: request 0x7E1 is answered on 0x7E9, never on 0x7E8.
        var frames = new List<CanFrame>();
        Iso.Send(frames, 0x7E1, "22 F1 90", 0);
        Iso.Send(frames, 0x7E8, "62 F1 90 41", 5);
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.DoesNotContain(transactions, t => t.Outcome == DiagnosticOutcome.Positive);
        Assert.Contains(transactions, t => t.Outcome == DiagnosticOutcome.NoResponse);
        Assert.Contains(transactions, t => t.Outcome == DiagnosticOutcome.OrphanResponse);
    }

    [Fact]
    public void PhysicalRequestIsLabelledPhysicalAndFunctionalRequestFunctional()
    {
        var physical = Run("3E 00", "7E 00");
        Assert.Contains("physical", physical.Endpoint.Addressing, StringComparison.OrdinalIgnoreCase);

        var frames = new List<CanFrame>();
        Iso.Send(frames, 0x7DF, "3E 80", 0);
        var functional = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Contains("functional", functional.Endpoint.Addressing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physical", functional.Endpoint.Addressing, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DiagnosticOutcome.SuppressedResponse, functional.Outcome);
    }

    [Fact]
    public void FunctionalRequestIsAnsweredByEachEcuAsItsOwnTransaction()
    {
        // ISO 15765-4: functional 0x7DF, responses on 0x7E8 and 0x7E9.
        var frames = new List<CanFrame>();
        Iso.Send(frames, 0x7DF, "10 03", 0);
        Iso.Send(frames, 0x7E8, "50 03 00 32 01 F4", 8);
        Iso.Send(frames, 0x7E9, "50 03 00 32 01 F4", 12);
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(2, transactions.Count);
        Assert.All(transactions, t => Assert.Equal(DiagnosticOutcome.Positive, t.Outcome));
        Assert.Equal(new[] { "ECU 0", "ECU 1" }, transactions.Select(t => t.Endpoint.Ecu).OrderBy(x => x));
        Assert.Equal(8.0, transactions.Single(t => t.Responses[0].Identifier == 0x7E8).ResponseTimeMilliseconds!.Value, 6);
        Assert.Equal(12.0, transactions.Single(t => t.Responses[0].Identifier == 0x7E9).ResponseTimeMilliseconds!.Value, 6);
    }

    [Fact]
    public void NormalFixedAddressing29BitUses0x18DAWithTargetThenSourceAddress()
    {
        // ISO 15765-2 10.3.2 (normal fixed): CAN ID 0x18DA TA SA. Tester 0xF1 -> ECU 0x10: 0x18DA10F1;
        // reply from ECU 0x10 to tester 0xF1: 0x18DAF110.
        var frames = new List<CanFrame>
        {
            Iso.F(0x18DA10F1, "03 22 F1 90 AA AA AA AA", 0, flags: CanFrameFlags.Extended),
            Iso.F(0x18DAF110, "05 62 F1 90 41 42 AA AA", 4, flags: CanFrameFlags.Extended)
        };
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal("0xF1", transaction.Endpoint.Tester);
        Assert.Equal("0x10", transaction.Endpoint.Ecu);
        Assert.Equal(4.0, transaction.ResponseTimeMilliseconds!.Value, 6);
    }

    [Fact]
    public void NormalFixedAddressingResponseFromADifferentSourceAddressIsNotPaired()
    {
        var frames = new List<CanFrame>
        {
            Iso.F(0x18DA10F1, "03 22 F1 90 AA AA AA AA", 0, flags: CanFrameFlags.Extended),
            Iso.F(0x18DAF111, "05 62 F1 90 41 42 AA AA", 4, flags: CanFrameFlags.Extended)
        };
        Assert.DoesNotContain(UdsTransactionAnalyzer.Analyze(frames), t => t.Outcome == DiagnosticOutcome.Positive);
    }

    [Fact]
    public void FunctionalRequest29BitUses0x18DBAndIsAnsweredPhysically()
    {
        // OBD functional: 0x18DB33F1 (TA 0x33, SA tester F1); ECU 0x10 answers on 0x18DAF110.
        var frames = new List<CanFrame>
        {
            Iso.F(0x18DB33F1, "02 10 03 AA AA AA AA AA", 0, flags: CanFrameFlags.Extended),
            Iso.F(0x18DAF110, "06 50 03 00 32 01 F4 AA", 6, flags: CanFrameFlags.Extended)
        };
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(6.0, transaction.ResponseTimeMilliseconds!.Value, 6);
    }

    [Fact]
    public void ElevenAndTwentyNineBitIdentifiersWithTheSameNumberAreDifferentChannelsOfTraffic()
    {
        var frames = new List<CanFrame>
        {
            Iso.F(0x7E0, "03 22 F1 90 AA AA AA AA", 0),
            new CanFrame(0, 0x7E8, Iso.H("05 62 F1 90 41 42 AA AA"), 4 * Iso.Ms, CanFrameFlags.Extended)
        };
        Assert.DoesNotContain(UdsTransactionAnalyzer.Analyze(frames), t => t.Outcome == DiagnosticOutcome.Positive);
    }

    // ---------------- P2 / P2* inference (14229-1 8.8, Table 'timing parameter') ----------------

    private static IReadOnlyList<DiagnosticTimingFinding> Timing(List<CanFrame> frames) =>
        DiagnosticTimingAnalyzer.Analyze(UdsTransactionAnalyzer.Analyze(frames), frames).Findings;

    [Fact]
    public void FirstResponseWithinDefaultP2Server50MsIsNotFlagged()
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "22 F1 90", 0);
        Iso.Send(frames, Ecu, "62 F1 90 41", 50);
        Assert.DoesNotContain(Timing(frames), f => f.RuleId == "TIM-001");
    }

    [Fact]
    public void FirstResponseLaterThanDefaultP2Server50MsIsFlaggedWithMeasuredGap()
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "22 F1 90", 0);
        Iso.Send(frames, Ecu, "62 F1 90 41", 60);
        var finding = Assert.Single(Timing(frames), f => f.RuleId == "TIM-001");
        Assert.Equal(60.0, finding.MeasuredMilliseconds!.Value, 6);
        Assert.Equal(50.0, finding.LimitMilliseconds!.Value, 6);
    }

    [Fact]
    public void AnnouncedP2FromSessionControlResponseReplacesTheDefaultForLaterRequests()
    {
        // 50 03 | P2=0x0014=20 ms | P2*=0x0064*10 ms=1000 ms. A later 30 ms answer violates 20 ms.
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "10 03", 0);
        Iso.Send(frames, Ecu, "50 03 00 14 00 64", 5);
        Iso.Send(frames, Tester, "22 F1 90", 100);
        Iso.Send(frames, Ecu, "62 F1 90 41", 130);
        var finding = Assert.Single(Timing(frames), f => f.RuleId == "TIM-001");
        Assert.Equal(30.0, finding.MeasuredMilliseconds!.Value, 6);
        Assert.Equal(20.0, finding.LimitMilliseconds!.Value, 6);
    }

    [Fact]
    public void PendingFollowedByResponseWithinDefaultP2StarOf5000MsIsNotFlagged()
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "31 01 FF 00", 0);
        Iso.Send(frames, Ecu, "7F 31 78", 20);
        Iso.Send(frames, Ecu, "71 01 FF 00", 5000 + 20);
        Assert.DoesNotContain(Timing(frames), f => f.RuleId is "TIM-001" or "TIM-002");
    }

    [Fact]
    public void ResponseMoreThanP2StarAfterPendingIsFlagged()
    {
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "31 01 FF 00", 0);
        Iso.Send(frames, Ecu, "7F 31 78", 20);
        Iso.Send(frames, Ecu, "71 01 FF 00", 6020);
        var finding = Assert.Single(Timing(frames), f => f.RuleId == "TIM-002");
        Assert.Equal(6000.0, finding.MeasuredMilliseconds!.Value, 6);
        Assert.Equal(5000.0, finding.LimitMilliseconds!.Value, 6);
    }

    [Fact]
    public void AnnouncedP2StarIsAppliedInTenMillisecondUnits()
    {
        // P2* field 0x0064 = 100 -> 1000 ms. A 1200 ms wait after 0x78 violates it.
        var frames = new List<CanFrame>();
        Iso.Send(frames, Tester, "10 03", 0);
        Iso.Send(frames, Ecu, "50 03 00 32 00 64", 5);
        Iso.Send(frames, Tester, "31 01 FF 00", 100);
        Iso.Send(frames, Ecu, "7F 31 78", 120);
        Iso.Send(frames, Ecu, "71 01 FF 00", 1320);
        var finding = Assert.Single(Timing(frames), f => f.RuleId == "TIM-002");
        Assert.Equal(1200.0, finding.MeasuredMilliseconds!.Value, 6);
        Assert.Equal(1000.0, finding.LimitMilliseconds!.Value, 6);
    }
}
