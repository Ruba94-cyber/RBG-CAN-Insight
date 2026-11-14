using System.Buffers.Binary;
using RbgCanInsight.Analysis;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class J1939TransportTests
{
    private static uint Id(byte pf, byte ps, byte sa) => (6u << 26) | ((uint)pf << 16) | ((uint)ps << 8) | sa;

    private static CanFrame F(uint id, byte[] data, long ms, long seq) =>
        new(0, id, data, ms * 1_000_000, CanFrameFlags.Extended, seq);

    private static CanFrame Cm(byte sa, byte da, long ms, long seq, params byte[] d) => F(Id(0xEC, da, sa), d, ms, seq);

    private static CanFrame Dt(byte sa, byte da, byte n, byte[] payload, long ms, long seq)
    {
        var d = new byte[8];
        Array.Fill(d, (byte)0xFF);
        d[0] = n;
        for (var i = 0; i < 7 && (n - 1) * 7 + i < payload.Length; i++) d[i + 1] = payload[(n - 1) * 7 + i];
        return F(Id(0xEB, da, sa), d, ms, seq);
    }

    private static byte[] Seq(int n) => Enumerable.Range(0, n).Select(i => (byte)(i + 1)).ToArray();

    [Fact]
    public void BamTwentyBytesReassemblesComplete()
    {
        var p = Seq(20);
        var frames = new List<CanFrame>
        {
            Cm(0x10, 0xFF, 0, 100, 32, 20, 0, 3, 0xFF, 0xEC, 0xFE, 0x00),
            Dt(0x10, 0xFF, 1, p, 50, 101), Dt(0x10, 0xFF, 2, p, 100, 102), Dt(0x10, 0xFF, 3, p, 150, 103)
        };
        var m = Assert.Single(J1939TransportAnalyzer.Reassemble(frames));
        Assert.Equal(J1939TransportStatus.Complete, m.Status);
        Assert.Equal(J1939TransportKind.Bam, m.Kind);
        Assert.Equal(0xFEECu, m.Pgn);
        Assert.Equal(p, m.Payload);
        Assert.Equal(new[] { 0, 1, 2, 3 }, m.FrameIndices);
        Assert.Equal(new long[] { 100, 101, 102, 103 }, m.FrameSequences);
        Assert.Equal(0, m.StartTimestampNanoseconds);
        Assert.Equal(150_000_000, m.EndTimestampNanoseconds);
        Assert.Empty(m.MissingSequenceNumbers);
    }

    [Fact]
    public void BamOutOfOrderAndDuplicateStillComplete()
    {
        var p = Seq(15);
        var frames = new[]
        {
            Cm(1, 0xFF, 0, 0, 32, 15, 0, 3, 0xFF, 0x00, 0xF0, 0x00),
            Dt(1, 0xFF, 2, p, 10, 1), Dt(1, 0xFF, 1, p, 20, 2), Dt(1, 0xFF, 1, p, 30, 3), Dt(1, 0xFF, 3, p, 40, 4)
        };
        var m = Assert.Single(J1939TransportAnalyzer.Reassemble(frames));
        Assert.Equal(J1939TransportStatus.Complete, m.Status);
        Assert.True(m.OutOfOrder);
        Assert.Equal(new[] { 1 }, m.DuplicateSequenceNumbers);
        Assert.Equal(p, m.Payload);
    }

    [Fact]
    public void RtsCtsFlowWithDm1ReassemblesAndDecodes()
    {
        byte[] p = [0x04, 0xFF, 0x64, 0x00, 0x03, 0x05, 0xCD, 0xAB, 0x24, 0x82, 0x01, 0x00, 0x2B, 0x01];
        var frames = new[]
        {
            Cm(0x00, 0x20, 0, 0, 16, 14, 0, 2, 2, 0xCA, 0xFE, 0x00),
            Cm(0x20, 0x00, 5, 1, 17, 2, 1, 0xFF, 0xFF, 0xCA, 0xFE, 0x00),
            Dt(0x00, 0x20, 1, p, 10, 2), Dt(0x00, 0x20, 2, p, 20, 3),
            Cm(0x20, 0x00, 25, 4, 19, 14, 0, 2, 0xFF, 0xCA, 0xFE, 0x00)
        };
        var m = Assert.Single(J1939TransportAnalyzer.Reassemble(frames));
        Assert.Equal(J1939TransportKind.RtsCts, m.Kind);
        Assert.Equal(J1939TransportStatus.Complete, m.Status);
        Assert.Equal(p, m.Payload);
        Assert.Equal(new[] { 0, 1, 2, 3 }, m.FrameIndices);
        Assert.True(J1939DiagnosticDecoder.TryDecodeDm1(m.Payload, out var dm1));
        Assert.Equal(3, dm1!.Dtcs.Count);
    }

    [Fact]
    public void MissingPacketIsIncompleteWithNoFabricatedPayload()
    {
        var p = Seq(20);
        var frames = new[]
        {
            Cm(0x10, 0xFF, 0, 0, 32, 20, 0, 3, 0xFF, 0xEC, 0xFE, 0x00),
            Dt(0x10, 0xFF, 1, p, 10, 1), Dt(0x10, 0xFF, 3, p, 20, 2)
        };
        var m = Assert.Single(J1939TransportAnalyzer.Reassemble(frames));
        Assert.Equal(J1939TransportStatus.Incomplete, m.Status);
        Assert.Null(m.Payload);
        Assert.Equal(new[] { 2 }, m.MissingSequenceNumbers);
        Assert.Equal(2, m.Packets.Count);
    }

    [Fact]
    public void GapLongerThanBamTimeoutIsTimedOut()
    {
        var p = Seq(20);
        var frames = new[]
        {
            Cm(0x10, 0xFF, 0, 0, 32, 20, 0, 3, 0xFF, 0xEC, 0xFE, 0x00),
            Dt(0x10, 0xFF, 1, p, 10, 1), Dt(0x10, 0xFF, 2, p, 900, 2), Dt(0x10, 0xFF, 3, p, 910, 3)
        };
        var m = Assert.Single(J1939TransportAnalyzer.Reassemble(frames));
        Assert.Equal(J1939TransportStatus.TimedOut, m.Status);
        Assert.Null(m.Payload);
        Assert.Equal(new[] { 2, 3 }, m.MissingSequenceNumbers);
    }

    [Fact]
    public void RtsWithoutCtsTimesOutAfter1250Ms()
    {
        var frames = new[]
        {
            Cm(0, 0x20, 0, 0, 16, 14, 0, 2, 2, 0xCA, 0xFE, 0x00),
            F(Id(0xFE, 0x6C, 0x30), [1, 2, 3, 4, 5, 6, 7, 8], 2000, 1)
        };
        var m = Assert.Single(J1939TransportAnalyzer.Reassemble(frames));
        Assert.Equal(J1939TransportStatus.TimedOut, m.Status);
    }

    [Fact]
    public void AbortEndsSessionWithReason()
    {
        var p = Seq(14);
        var frames = new[]
        {
            Cm(0x00, 0x20, 0, 0, 16, 14, 0, 2, 2, 0xCA, 0xFE, 0x00),
            Dt(0x00, 0x20, 1, p, 10, 1),
            Cm(0x20, 0x00, 15, 2, 255, 3, 0xFF, 0xFF, 0xFF, 0xCA, 0xFE, 0x00)
        };
        var m = Assert.Single(J1939TransportAnalyzer.Reassemble(frames));
        Assert.Equal(J1939TransportStatus.Aborted, m.Status);
        Assert.Equal((byte)3, m.AbortReason);
        Assert.Null(m.Payload);
        Assert.Equal(new[] { 0, 1, 2 }, m.FrameIndices);
    }

    [Fact]
    public void Dm1WithTwoDtcsDecodes()
    {
        byte[] p = [0x14, 0xFF, 0x64, 0x00, 0x03, 0x05, 0xCD, 0xAB, 0x24, 0x82];
        Assert.True(J1939DiagnosticDecoder.TryDecodeDm1(p, out var m));
        Assert.False(m!.NoDtcs);
        Assert.Equal(J1939LampState.Off, m.Lamps.MalfunctionIndicator);
        Assert.Equal(J1939LampState.On, m.Lamps.RedStop);
        Assert.Equal(J1939LampState.On, m.Lamps.AmberWarning);
        Assert.Equal(J1939LampState.Off, m.Lamps.Protect);
        Assert.Equal(2, m.Dtcs.Count);
        Assert.Equal((100, 3, false, 5), (m.Dtcs[0].Spn, m.Dtcs[0].Fmi, m.Dtcs[0].ConversionMethod, m.Dtcs[0].OccurrenceCount));
        Assert.Equal((0x1ABCD, 4, true, 2), (m.Dtcs[1].Spn, m.Dtcs[1].Fmi, m.Dtcs[1].ConversionMethod, m.Dtcs[1].OccurrenceCount));
    }

    [Fact]
    public void Dm1NoActiveDtcsAndDm2()
    {
        Assert.True(J1939DiagnosticDecoder.TryDecodeDm1([0x00, 0xFF, 0, 0, 0, 0, 0xFF, 0xFF], out var m));
        Assert.True(m!.NoDtcs);
        Assert.Empty(m.Dtcs);
        Assert.True(J1939DiagnosticDecoder.TryDecodeDm2([0x00, 0xFF, 0x0A, 0x00, 0x11, 0x01, 0xFF, 0xFF], out var m2));
        Assert.Equal(J1939DiagnosticDecoder.Dm2Pgn, m2!.Pgn);
        Assert.Equal((10, 17, 1), (m2.Dtcs[0].Spn, m2.Dtcs[0].Fmi, m2.Dtcs[0].OccurrenceCount));
        Assert.False(J1939DiagnosticDecoder.TryDecodeDm1([0x00], out _));
        Assert.False(J1939DiagnosticDecoder.TryDecodeDm1([0x00, 0xFF, 1, 2, 3], out _));
    }

    [Fact]
    public void AddressClaimNameDecodes()
    {
        ulong v = 0x12345UL | (0x123UL << 21) | (1UL << 32) | (2UL << 35) | (0x1DUL << 40) |
                  (5UL << 49) | (3UL << 56) | (2UL << 60) | (1UL << 63);
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        var claim = J1939DiagnosticDecoder.DecodeAddressClaim(F(Id(0xEE, 0xFF, 0x80), b, 0, 0));
        Assert.NotNull(claim);
        Assert.Equal(0x80, claim!.ClaimedAddress);
        var n = claim.Name;
        Assert.True(n.ArbitraryAddressCapable);
        Assert.Equal(2, n.IndustryGroup);
        Assert.Equal("Agricultural and Forestry", n.IndustryGroupName);
        Assert.Equal(3, n.VehicleSystemInstance);
        Assert.Equal(5, n.VehicleSystem);
        Assert.Equal(0x1D, n.Function);
        Assert.Equal(2, n.FunctionInstance);
        Assert.Equal(1, n.EcuInstance);
        Assert.Equal(0x123, n.ManufacturerCode);
        Assert.Equal(0x12345, n.IdentityNumber);
        Assert.Equal(1, J1939DiagnosticDecoder.DecodeAddressClaim([1, 0, 0, 0, 0, 0, 0, 0])!.IdentityNumber);
        Assert.Null(J1939DiagnosticDecoder.DecodeAddressClaim([1, 2, 3]));
    }
}
