using System.Buffers.Binary;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class PcapngCanImporterTests
{
    // ------------------------------------------------------------- builders

    private static void U16(List<byte> o, ushort v, bool le)
    {
        Span<byte> s = stackalloc byte[2];
        if (le) BinaryPrimitives.WriteUInt16LittleEndian(s, v); else BinaryPrimitives.WriteUInt16BigEndian(s, v);
        o.AddRange(s.ToArray());
    }

    private static void U32(List<byte> o, uint v, bool le)
    {
        Span<byte> s = stackalloc byte[4];
        if (le) BinaryPrimitives.WriteUInt32LittleEndian(s, v); else BinaryPrimitives.WriteUInt32BigEndian(s, v);
        o.AddRange(s.ToArray());
    }

    private static byte[] Block(uint type, bool le, Action<List<byte>> body)
    {
        var content = new List<byte>();
        body(content);
        while (content.Count % 4 != 0) content.Add(0);
        var block = new List<byte>();
        U32(block, type, le);
        U32(block, (uint)(content.Count + 12), le);
        block.AddRange(content);
        U32(block, (uint)(content.Count + 12), le);
        return block.ToArray();
    }

    private static byte[] Shb(bool le) => Block(0x0A0D0D0A, le, b =>
    {
        U32(b, 0x1A2B3C4D, le);
        U16(b, 1, le); U16(b, 0, le);
        b.AddRange(Enumerable.Repeat((byte)0xFF, 8)); // section length unknown
    });

    private static byte[] Idb(bool le, ushort linkType, byte? tsresol = null) => Block(1, le, b =>
    {
        U16(b, linkType, le); U16(b, 0, le); U32(b, 0, le);
        if (tsresol is { } r)
        {
            U16(b, 9, le); U16(b, 1, le); b.Add(r); b.AddRange(new byte[3]);
            U16(b, 0, le); U16(b, 0, le);
        }
    });

    private static byte[] Epb(bool le, uint iface, ulong ticks, byte[] packet, uint? original = null) =>
        Block(6, le, b =>
        {
            U32(b, iface, le); U32(b, (uint)(ticks >> 32), le); U32(b, (uint)ticks, le);
            U32(b, (uint)packet.Length, le); U32(b, original ?? (uint)packet.Length, le);
            b.AddRange(packet);
        });

    /// <summary>struct can_frame as seen with LINKTYPE_CAN_SOCKETCAN (id big endian).</summary>
    private static byte[] Classic(uint canId, byte len, params byte[] data)
    {
        var p = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(p, canId);
        p[4] = len;
        data.CopyTo(p, 8);
        return p;
    }

    private static byte[] Fd(uint canId, byte len, byte fdFlags, params byte[] data)
    {
        var p = new byte[72];
        BinaryPrimitives.WriteUInt32BigEndian(p, canId);
        p[4] = len; p[5] = fdFlags;
        data.CopyTo(p, 8);
        return p;
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static async Task<(List<CanFrame> Frames, TraceImportHealthSnapshot Health)> Read(byte[] file)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-pcapng-{Guid.NewGuid():N}.pcapng");
        File.WriteAllBytes(path, file);
        try
        {
            var health = new TraceImportHealth();
            var frames = new List<CanFrame>();
            await foreach (var frame in PcapngCanImporter.ReadAsync(path, diagnostics: health))
                frames.Add(frame);
            return (frames, health.Snapshot());
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------- tests

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadsClassicExtendedRemoteAndErrorFramesInEitherByteOrder(bool le)
    {
        var file = Cat(Shb(le), Idb(le, 227), Idb(le, 227),
            Epb(le, 0, 1_500_000, Classic(0x123, 3, 1, 2, 3)),                       // 1.5 s in microseconds
            Epb(le, 1, 2_000_000, Classic(0x8000_0000 | 0x18FEF100, 8, 1, 2, 3, 4, 5, 6, 7, 8)),
            Epb(le, 0, 3_000_000, Classic(0x4000_0000 | 0x321, 4)),
            Epb(le, 0, 4_000_000, Classic(0x2000_0000 | 0x0000_0004, 8, 0, 0, 0, 0, 0, 0, 0, 0)));

        var (frames, health) = await Read(file);

        Assert.Equal(4, frames.Count);
        Assert.Equal(0, frames[0].Channel);
        Assert.Equal(0x123u, frames[0].Identifier);
        Assert.Equal(1_500_000_000L, frames[0].TimestampNanoseconds);
        Assert.Equal(new byte[] { 1, 2, 3 }, frames[0].GetPayload());
        Assert.Equal(1, frames[1].Channel);
        Assert.True(frames[1].IsExtended);
        Assert.Equal(0x18FEF100u, frames[1].Identifier);
        Assert.Equal(CanFrameFlags.Remote, frames[2].Flags);
        Assert.Equal(4, frames[2].DeclaredDataLength);
        Assert.Empty(frames[2].GetPayload());
        Assert.True(frames[3].IsError);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, frames.Select(f => f.Sequence).ToArray());
        Assert.Equal(0, health.RejectedRecords);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadsCanFdFramesWithBitRateSwitch(bool le)
    {
        var payload = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var file = Cat(Shb(le), Idb(le, 227),
            Epb(le, 0, 10, Fd(0x456, 12, 0x01 | 0x04, payload)),
            Epb(le, 0, 20, Fd(0x8000_0000 | 0x1ABCDE, 2, 0x04, 0xAA, 0xBB)));

        var (frames, health) = await Read(file);

        Assert.Equal(2, frames.Count);
        Assert.Equal(CanFrameFlags.FlexibleDataRate | CanFrameFlags.BitRateSwitch, frames[0].Flags);
        Assert.Equal(payload, frames[0].GetPayload());
        Assert.Equal(10_000L, frames[0].TimestampNanoseconds);
        Assert.Equal(CanFrameFlags.FlexibleDataRate | CanFrameFlags.Extended, frames[1].Flags);
        Assert.Equal(0, health.RejectedRecords);
    }

    [Theory]
    [InlineData((byte)9, 1_000_000_000UL, 1_000_000_000L)]   // 10^-9: nanoseconds
    [InlineData((byte)3, 1_234UL, 1_234_000_000L)]            // 10^-3: milliseconds
    [InlineData((byte)0, 7UL, 7_000_000_000L)]                // seconds
    [InlineData((byte)138, 1024UL, 1_000_000_000L)]     // 2^-10: 1024 ticks = 1 s
    public async Task HonoursInterfaceTimestampResolution(byte tsresol, ulong ticks, long expectedNs)
    {
        var file = Cat(Shb(true), Idb(true, 227, tsresol), Epb(true, 0, ticks, Classic(0x1, 0)));
        var (frames, _) = await Read(file);
        Assert.Equal(expectedNs, Assert.Single(frames).TimestampNanoseconds);
    }

    [Fact]
    public async Task SixtyFourBitTimestampSpansHighAndLowWords()
    {
        var ticks = (5UL << 32) | 7UL; // microseconds
        var file = Cat(Shb(false), Idb(false, 227), Epb(false, 0, ticks, Classic(0x1, 0)));
        var (frames, _) = await Read(file);
        Assert.Equal((long)ticks * 1000, Assert.Single(frames).TimestampNanoseconds);
    }

    [Fact]
    public async Task SkipsNonCanInterfacesAndReportsCounts()
    {
        var file = Cat(Shb(true), Idb(true, 1 /* Ethernet */), Idb(true, 227),
            Epb(true, 0, 1, new byte[60]),
            Epb(true, 0, 2, new byte[60]),
            Epb(true, 1, 3, Classic(0x100, 1, 0x42)));

        var (frames, health) = await Read(file);

        var frame = Assert.Single(frames);
        Assert.Equal(1, frame.Channel);
        Assert.Equal(2, health.UnsupportedObjectRecords);
        Assert.Contains(health.IssueExamples, i => i.Code == "PCAPNG_NON_CAN_LINKTYPE");
    }

    [Fact]
    public async Task RejectsMalformedPacketsWithoutFabricatingFrames()
    {
        var file = Cat(Shb(true), Idb(true, 227),
            Epb(true, 0, 1, Classic(0x100, 9)),                       // classic length > 8
            Epb(true, 0, 2, Classic(0x800, 1, 1)),                    // 11-bit frame with bits above 10
            Epb(true, 0, 3, new byte[20]),                            // neither 16 nor 72 bytes
            Epb(true, 0, 4, Classic(0x100, 1, 1), original: 40),     // snaplen-truncated
            Epb(true, 0, 5, Fd(0x100, 9, 0x04)),                     // 9 is no FD length
            Epb(true, 0, 6, Classic(0x7FF, 1, 0x55)));

        var (frames, health) = await Read(file);

        Assert.Equal(0x7FFu, Assert.Single(frames).Identifier);
        Assert.Equal(5, health.RejectedRecords);
    }

    [Fact]
    public async Task EsiBitKeepsPayloadAndRecordsAWarning()
    {
        var file = Cat(Shb(true), Idb(true, 227), Epb(true, 0, 1, Fd(0x100, 1, 0x04 | 0x02, 0x9A)));
        var (frames, health) = await Read(file);
        Assert.Equal(new byte[] { 0x9A }, Assert.Single(frames).GetPayload());
        Assert.Contains(health.IssueExamples, i => i.Code == "PCAPNG_ESI_NOT_REPRESENTED");
    }

    [Fact]
    public async Task ReadsClassicPcapInBothByteOrdersAndPrecisions()
    {
        foreach (var (le, nano) in new[] { (true, false), (false, false), (true, true), (false, true) })
        {
            var o = new List<byte>();
            U32(o, nano ? 0xA1B23C4D : 0xA1B2C3D4, le);
            U16(o, 2, le); U16(o, 4, le); U32(o, 0, le); U32(o, 0, le); U32(o, 65535, le); U32(o, 227, le);
            U32(o, 3, le); U32(o, 250, le); U32(o, 16, le); U32(o, 16, le);
            o.AddRange(Classic(0x123, 2, 0xDE, 0xAD));
            var (frames, _) = await Read(o.ToArray());
            var frame = Assert.Single(frames);
            Assert.Equal(3_000_000_000L + (nano ? 250 : 250_000), frame.TimestampNanoseconds);
            Assert.Equal(new byte[] { 0xDE, 0xAD }, frame.GetPayload());
        }
    }

    [Fact]
    public async Task TruncatedFileRaisesInvalidDataException()
    {
        var full = Cat(Shb(true), Idb(true, 227), Epb(true, 0, 1, Classic(0x100, 1, 1)));
        foreach (var cut in new[] { 5, 20, full.Length - 30, full.Length - 1 })
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(full[..cut]));
    }

    [Fact]
    public async Task InconsistentOrUnknownStructuresRaiseInvalidDataException()
    {
        var epb = Epb(true, 0, 1, Classic(0x100, 1, 1));
        var badTrailer = (byte[])epb.Clone();
        badTrailer[^4] ^= 0x7F;
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Cat(Shb(true), Idb(true, 227), badTrailer)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Cat(Shb(true), epb))); // no interface
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        var badMagic = Shb(true);
        badMagic[8] ^= 0xFF;
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(badMagic));
    }
}
