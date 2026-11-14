using System.Buffers.Binary;
using RbgCanInsight.Analysis.Buses;
using RbgCanInsight.Cli;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>CAN XL import from hand-built SocketCAN data (no real CAN XL capture was available).
/// Layouts: linux/can.h canxl_frame, tcpdump.org LINKTYPE_CAN_SOCKETCAN, can-utils lib.c.</summary>
public sealed class CanXlImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rbgxl").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------------------------------------------------------------- builders

    /// <summary>LINKTYPE_CAN_SOCKETCAN CAN XL packet: prio/VCID BE, flags, SDT, len LE, AF LE, payload.</summary>
    private static byte[] XlPacket(uint prio, byte vcid, byte flags, byte sdt, uint af, byte[] payload, int declaredLen = -1, int pad = 0)
    {
        var p = new byte[12 + payload.Length + pad];
        BinaryPrimitives.WriteUInt32BigEndian(p, prio | ((uint)vcid << 16));
        p[4] = flags; p[5] = sdt;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)(declaredLen < 0 ? payload.Length : declaredLen));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), af);
        payload.CopyTo(p, 12);
        return p;
    }

    private static byte[] ClassicPacket(uint id, params byte[] data)
    {
        var p = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(p, id);
        p[4] = (byte)data.Length;
        data.CopyTo(p, 8);
        return p;
    }

    private static byte[] ClassicPcap(params (uint Sec, uint Usec, byte[] Packet)[] packets)
    {
        var o = new List<byte>();
        o.AddRange(BitConverter.GetBytes(0xA1B2C3D4u));
        o.AddRange(BitConverter.GetBytes((ushort)2)); o.AddRange(BitConverter.GetBytes((ushort)4));
        o.AddRange(new byte[8]);
        o.AddRange(BitConverter.GetBytes(65535u)); o.AddRange(BitConverter.GetBytes(227u));
        foreach (var (s, us, pk) in packets)
        {
            o.AddRange(BitConverter.GetBytes(s)); o.AddRange(BitConverter.GetBytes(us));
            o.AddRange(BitConverter.GetBytes((uint)pk.Length)); o.AddRange(BitConverter.GetBytes((uint)pk.Length));
            o.AddRange(pk);
        }
        return o.ToArray();
    }

    private static byte[] Pcapng(params byte[][] packets)
    {
        static byte[] Block(uint type, byte[] content)
        {
            var padded = content.Concat(new byte[(4 - content.Length % 4) % 4]).ToArray();
            var o = new List<byte>();
            o.AddRange(BitConverter.GetBytes(type));
            o.AddRange(BitConverter.GetBytes((uint)(padded.Length + 12)));
            o.AddRange(padded);
            o.AddRange(BitConverter.GetBytes((uint)(padded.Length + 12)));
            return o.ToArray();
        }
        var shb = new List<byte>();
        shb.AddRange(BitConverter.GetBytes(0x1A2B3C4Du)); shb.AddRange(BitConverter.GetBytes((ushort)1));
        shb.AddRange(BitConverter.GetBytes((ushort)0)); shb.AddRange(Enumerable.Repeat((byte)0xFF, 8));
        var idb = new List<byte>();
        idb.AddRange(BitConverter.GetBytes((ushort)227)); idb.AddRange(new byte[2]); idb.AddRange(BitConverter.GetBytes(0u));
        var file = new List<byte>();
        file.AddRange(Block(0x0A0D0D0A, shb.ToArray()));
        file.AddRange(Block(1, idb.ToArray()));
        var t = 1_000_000UL;
        foreach (var pk in packets)
        {
            var epb = new List<byte>();
            epb.AddRange(BitConverter.GetBytes(0u));
            epb.AddRange(BitConverter.GetBytes((uint)(t >> 32))); epb.AddRange(BitConverter.GetBytes((uint)t));
            epb.AddRange(BitConverter.GetBytes((uint)pk.Length)); epb.AddRange(BitConverter.GetBytes((uint)pk.Length));
            epb.AddRange(pk);
            file.AddRange(Block(6, epb.ToArray()));
            t += 500;
        }
        return file.ToArray();
    }

    private async Task<TraceImportResult> Import(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, bytes);
        return await TraceImporter.ReadAllAsync(path);
    }

    private async Task<TraceImportResult> ImportText(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, text);
        return await TraceImporter.ReadAllAsync(path);
    }

    // ---------------------------------------------------------------- pcap / pcapng

    [Fact]
    public async Task PcapngImportsXlFrameAndKeepsClassicOnTheCanPath()
    {
        var payload = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var r = await Import("a.pcapng", Pcapng(
            ClassicPacket(0x123, 1, 2),
            XlPacket(0x1A5, 0x07, 0x81, 0x03, 0xDEADBEEF, payload, pad: 3)));
        Assert.Single(r.CanFrames);
        var xl = Assert.Single(r.BusFrames.CanXl);
        Assert.Equal(0x1A5u, xl.PriorityId);
        Assert.Equal(7, xl.Vcid);
        Assert.Equal(3, xl.Sdt);
        Assert.True(xl.Sec);
        Assert.False(xl.Rrs);
        Assert.Equal(0xDEADBEEFu, xl.AcceptanceField);
        Assert.Equal(payload, xl.Payload);
        Assert.Equal(0, xl.Channel);
        Assert.Equal(1_000_500_000, xl.TimestampNanoseconds);
        Assert.Contains("1 CAN XL frames", r.Health.Summary);
        Assert.Equal(1, r.Health.CanXlFrames);
    }

    [Fact]
    public async Task ClassicPcapImportsMaxLengthAndMinLengthAndRrs()
    {
        var r = await Import("b.pcap", ClassicPcap(
            (5, 250_000, XlPacket(0x7FF, 0xFF, 0x82, 0x01, 1, new byte[2048])),
            (6, 0, XlPacket(0, 0, 0x80, 0xAB, 0, [0x42]))));
        Assert.Equal(2, r.BusFrames.CanXl.Count);
        Assert.Equal(2048, r.BusFrames.CanXl[0].Payload.Length);
        Assert.Equal(0x7FFu, r.BusFrames.CanXl[0].PriorityId);
        Assert.Equal(0xFF, r.BusFrames.CanXl[0].Vcid);
        Assert.True(r.BusFrames.CanXl[0].Rrs);
        Assert.Equal(5_250_000_000, r.BusFrames.CanXl[0].TimestampNanoseconds);
        Assert.Equal(new byte[] { 0x42 }, r.BusFrames.CanXl[1].Payload);
        Assert.Equal(0xAB, r.BusFrames.CanXl[1].Sdt);
    }

    [Fact]
    public async Task InvalidXlPacketsAreRejectedAndCounted()
    {
        var good = XlPacket(1, 1, 0x80, 1, 1, [1, 2, 3]);
        var r = await Import("c.pcap", ClassicPcap(
            (1, 0, XlPacket(1, 1, 0x80, 1, 1, [1], declaredLen: 0)),          // length 0
            (1, 1, XlPacket(1, 1, 0x80, 1, 1, [1], declaredLen: 2049)),       // length 2049
            (1, 2, XlPacket(1, 1, 0x80, 1, 1, [1, 2], declaredLen: 5)),       // length exceeds data
            (1, 3, XlPacket(1, 1, 0x84, 1, 1, [1])),                          // undefined flag bit
            (1, 4, new byte[] { 0, 0, 0, 1, 0x80, 1, 1, 0 }),                  // < 13 bytes
            (1, 5, good)));
        Assert.Single(r.BusFrames.CanXl);
        Assert.Equal(5, r.Health.RejectedRecords);
    }

    [Fact]
    public async Task TruncatedAndGarbagePcapsFailLoudlyOrAreCounted()
    {
        var pcapng = Pcapng(XlPacket(1, 1, 0x80, 1, 1, [1, 2, 3, 4]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Import("t.pcapng", pcapng[..^7]));
        var pcap = ClassicPcap((1, 0, XlPacket(1, 1, 0x80, 1, 1, [1, 2, 3, 4])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Import("t.pcap", pcap[..^3]));

        var rng = new Random(1234);
        for (var i = 0; i < 40; i++)
        {
            var junk = new byte[rng.Next(13, 200)];
            rng.NextBytes(junk);
            junk[4] |= 0x80; // looks like XL
            var file = ClassicPcap((1, 0, junk));
            var r = await Import($"g{i}.pcap", file); // must never throw or exceed bounds
            Assert.True(r.BusFrames.CanXl.Count <= 1);
            foreach (var f in r.BusFrames.CanXl) Assert.InRange(f.Payload.Length, 1, 2048);
        }
    }

    // ---------------------------------------------------------------- candump -L

    [Fact]
    public async Task CandumpLogImportsXlLinesWithSeparatorsAndCanLinesTogether()
    {
        var r = await ImportText("x.log",
            "(1.500000) can0 123#1122\n" +
            "(1.600000) can1 03123#81:03:0000ABCD#DE.AD.BE.EF\n" +
            "(1.700000) can1 00001#80:01:00000000#00112233445566778899\n");
        Assert.Single(r.CanFrames);
        Assert.Equal(0x123u, r.CanFrames[0].Identifier);
        Assert.Equal(2, r.BusFrames.CanXl.Count);
        var a = r.BusFrames.CanXl[0];
        Assert.Equal(0x123u, a.PriorityId);
        Assert.Equal(3, a.Vcid);
        Assert.True(a.Sec);
        Assert.Equal(3, a.Sdt);
        Assert.Equal(0xABCDu, a.AcceptanceField);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, a.Payload);
        Assert.Equal(1, a.Channel);
        Assert.Equal(1_600_000_000, a.TimestampNanoseconds);
        Assert.Equal(10, r.BusFrames.CanXl[1].Payload.Length);
        Assert.Equal(0, r.Health.RejectedRecords);
    }

    [Theory]
    [InlineData("(1.0) can0 00123#80:01:00000000#")]                 // zero data bytes
    [InlineData("(1.0) can0 00123#80:01:00000000#ABC")]              // odd hex digits
    [InlineData("(1.0) can0 00123#80:01:00000000#AA.B")]             // separator inside a byte
    [InlineData("(1.0) can0 00123#84:01:00000000#AA")]               // undefined flag
    [InlineData("(1.0) can0 00123#80:01:0000000#AA")]                // short AF
    [InlineData("(1.0) can0 00123#80:01:00000000#GG")]               // non-hex
    public async Task MalformedXlCandumpLinesAreRejectedNotGuessed(string line)
    {
        var r = await ImportText("m.log", line + "\n");
        Assert.Empty(r.BusFrames.CanXl);
        Assert.Empty(r.CanFrames);
        Assert.Equal(1, r.Health.RejectedRecords);
    }

    [Fact]
    public async Task CandumpPayloadAbove2048BytesIsRejectedAndExactly2048Accepted()
    {
        var ok = "(1.0) can0 00001#80:01:00000000#" + new string('A', 4096);
        var bad = "(1.0) can0 00001#80:01:00000000#" + new string('A', 4098);
        var r = await ImportText("big.log", ok + "\n" + bad + "\n");
        Assert.Single(r.BusFrames.CanXl);
        Assert.Equal(2048, r.BusFrames.CanXl[0].Payload.Length);
        Assert.Equal(1, r.Health.RejectedRecords);
    }

    [Fact]
    public async Task TruncatedLastCandumpLineIsRejected()
    {
        var r = await ImportText("tr.log", "(1.0) can0 00001#80:01:00000000#AABB\n(2.0) can0 00001#80:01:000");
        Assert.Single(r.BusFrames.CanXl);
        Assert.Equal(1, r.Health.RejectedRecords);
    }

    // ---------------------------------------------------------------- model / tables / CLI

    [Fact]
    public void SdtNamesExistOnlyForVerifiedValues()
    {
        Assert.Equal("Content-based addressing", CanXlSdt.Name(0x01));
        Assert.Equal("CAN CC / CAN FD tunneling", CanXlSdt.Name(0x03));
        foreach (var v in new byte[] { 0x00, 0x02, 0x04, 0x05, 0x06, 0xFF })
            Assert.Null(CanXlSdt.Name(v));
        Assert.True(CanXlFrame.IsValidPayloadLength(1));
        Assert.True(CanXlFrame.IsValidPayloadLength(2048));
        Assert.False(CanXlFrame.IsValidPayloadLength(0));
        Assert.False(CanXlFrame.IsValidPayloadLength(2049));
    }

    [Fact]
    public async Task CanXlListTableShowsRawSdtAndNameOnlyWhenKnown()
    {
        var r = await Import("tbl.pcap", ClassicPcap(
            (1, 0, XlPacket(5, 1, 0x80, 0x01, 0, [1])),
            (2, 0, XlPacket(5, 1, 0x80, 0x09, 0, [1]))));
        var table = BusReportTables.CanXlList(r.BusFrames.CanXl);
        var sdt = Array.IndexOf(table.Headers, "SDT");
        Assert.Equal("0x01 (Content-based addressing)", table.Rows[0][sdt]);
        Assert.Equal("0x09", table.Rows[1][sdt]);
    }

    [Fact]
    public async Task CliReportCanXlWritesCsv()
    {
        var path = Path.Combine(_dir, "cli.log");
        await File.WriteAllTextAsync(path, "(1.0) can0 00123#80:01:00000000#AABBCC\n");
        var o = new StringWriter();
        var code = await CliCommands.RunAsync(["report", "canxl", path, Path.Combine(_dir, "xl.csv")], o, new StringWriter());
        Assert.Equal(0, code);
        Assert.Contains("1 CAN XL record(s)", o.ToString());
        Assert.Contains("AA BB CC", await File.ReadAllTextAsync(Path.Combine(_dir, "xl.csv")));
    }
}
