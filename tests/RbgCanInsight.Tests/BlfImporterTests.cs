using System.Buffers.Binary;
using System.IO.Compression;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class BlfImporterTests
{
    [Fact]
    public async Task ReadsClassicCanMessagesFromCompressedContainer()
    {
        var objects = Concat(
            CanMessageObject(channel: 1, id: 0x1A0, timestampNs: 1_000_000, [0x11, 0x22, 0x33]),
            CanMessageObject(channel: 2, id: 0x18FEF100 | 0x8000_0000, timestampNs: 2_000_000, [0xAA]));
        var file = BuildBlf(LogContainer(objects, compress: true));

        var frames = await ReadAsync(file);

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x1A0u, frames[0].Identifier);
        Assert.Equal(0, frames[0].Channel);
        Assert.Equal(1_000_000, frames[0].TimestampNanoseconds);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, frames[0].GetPayload());
        Assert.True(frames[1].IsExtended);
        Assert.Equal(0x18FEF100u, frames[1].Identifier);
    }

    [Fact]
    public async Task ReadsUncompressedTopLevelObjects()
    {
        var file = BuildBlf(CanMessageObject(1, 0x7F, 5_000_000, [0x01, 0x02]));

        var frames = await ReadAsync(file);

        Assert.Single(frames);
        Assert.Equal(0x7Fu, frames[0].Identifier);
    }


    [Fact]
    public async Task ReadsV2HeaderEvenWhenObjectVersionIsZero()
    {
        // The LOBJ header version, not the object's version field, determines
        // whether the timestamp header consumes 16 or 24 bytes.
        var v2 = CanMessageObjectV2(1, 0x321, 7_000_000, [0x77, 0x88]);
        var frames = await ReadAsync(BuildBlf(LogContainer(v2, compress: true)));

        var frame = Assert.Single(frames);
        Assert.Equal(0x321u, frame.Identifier);
        Assert.Equal(7_000_000, frame.TimestampNanoseconds);
        Assert.Equal(new byte[] { 0x77, 0x88 }, frame.GetPayload());
    }

    [Fact]
    public async Task FindsNextObjectWithWriterStylePadding()
    {
        // Some BLF writers pad by objectSize % 4 instead of its complement.
        // The next LOBJ must still be found even after a 17-byte unknown object.
        using var stream = new MemoryStream();
        stream.Write(BuildBlf());
        var unknown = WrapObject(999, [0xAA]);
        Assert.Equal(17, unknown.Length);
        stream.Write(unknown);
        stream.WriteByte(0);
        stream.Write(CanMessageObject(1, 0x456, 9_000_000, [0x12]));

        var frame = Assert.Single(await ReadAsync(stream.ToArray()));
        Assert.Equal(0x456u, frame.Identifier);
    }

    private static byte[] CanMessageObjectV2(ushort channel, uint id, ulong timestampNs, byte[] data)
    {
        var body = new byte[24 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 0); // objectVersion, not headerVersion
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), timestampNs);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(24), channel);
        body[27] = (byte)data.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), id);
        data.CopyTo(body.AsSpan(32));
        return WrapObject(1, body, headerSize: 40, headerVersion: 2);
    }


    [Fact]
    public async Task BlfTimestampFlagsUseTenMicrosecondsOnlyWhenFlagIsOne()
    {
        var flagsZero = CanMessageObject(1, 0x101, 123456, [0x10], timestampFlags: 0);
        var flagsOne = CanMessageObject(1, 0x102, 123456, [0x20], timestampFlags: 1);
        var flagsTwo = CanMessageObject(1, 0x103, 123456, [0x30], timestampFlags: 2);
        var imported = await ReadAsync(BuildBlf(LogContainer(
            Concat(flagsZero, flagsOne, flagsTwo), compress: true)));

        Assert.Equal(3, imported.Count);
        Assert.Equal(123456, imported[0].TimestampNanoseconds);
        Assert.Equal(1_234_560_000L, imported[1].TimestampNanoseconds);
        Assert.Equal(123456, imported[2].TimestampNanoseconds);
    }

    [Fact]
    public async Task BlfTimestampOverflowIsRejected()
    {
        var illegal = CanMessageObject(1, 0x123, ulong.MaxValue, [0xFF], timestampFlags: 1);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ReadAsync(BuildBlf(LogContainer(illegal, compress: true))));
    }

    [Fact]
    public async Task ReadsCanFdMessage64FromZlibContainer()
    {
        var values = Enumerable.Range(1, 24).Select(i => (byte)i).ToArray();
        var body = new byte[16 + 40 + values.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), 42_000_000);
        // No Span local in an async C# 12 test method.
        body[16] = 1; // channel 1
        body[17] = 12; // 24-byte CAN FD DLC
        body[18] = 24;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), 0x18DAF110u | 0x8000_0000u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), 0x1000u | 0x2000u);
        values.CopyTo(body.AsSpan(56));
        var frame = Assert.Single(await ReadAsync(BuildBlf(
            LogContainer(WrapObject(101, body, headerSize: 32), compress: true))));

        Assert.Equal(0, frame.Channel);
        Assert.Equal(0x18DAF110u, frame.Identifier);
        Assert.Equal(42_000_000L, frame.TimestampNanoseconds);
        Assert.True(frame.IsExtended);
        Assert.True(frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate));
        Assert.True(frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch));
        Assert.Equal(values, frame.GetPayload());
    }

    [Fact]
    public async Task ReconstructsObjectsSplitAcrossCompressedAndRawContainers()
    {
        var obj = CanMessageObject(1, 0x456, 99_000_000, [0x11, 0x22]);
        var file = BuildBlf(
            LogContainer(obj[..19], compress: true),
            LogContainer(obj[19..], compress: false));

        var frame = Assert.Single(await ReadAsync(file));
        Assert.Equal(0x456u, frame.Identifier);
        Assert.Equal(new byte[] { 0x11, 0x22 }, frame.GetPayload());
    }

    [Fact]
    public async Task TruncatedBlfBodyIsNotSilentlyAccepted()
    {
        var file = BuildBlf(LogContainer(
            CanMessageObject(1, 0x100, 1_000_000, [0x01]), compress: false));
        var truncated = file[..^12];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await ReadAsync(truncated);
        });
    }

    [Fact]
    public async Task UnsupportedBlfCompressionIsReported()
    {
        var body = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 99);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await ReadAsync(BuildBlf(WrapObject(10, body)));
        });
    }

    [Fact]
    public async Task ImportHealthFlagsNonpaddingJunkInsideBlfContainerWithoutLosingFollowingFrames()
    {
        var first = CanMessageObject(1, 0x100, 1_000_000, [0x01]);
        var second = CanMessageObject(1, 0x101, 2_000_000, [0x02]);
        // A nonzero stray byte plus three zeroes between valid LOBJ records:
        // recover the second CAN frame, but the source is not fully clean.
        var content = Concat(first, [0xA5], second);
        var file = BuildBlf(LogContainer(content, compress: true));
        var (frames, health) = await ReadWithHealthAsync(file);
        Assert.Equal(2, frames.Count);
        Assert.True(health.Completed);
        Assert.True(health.RequiresReview);
        Assert.True(health.UnexpectedContainerBytes >= 4);
        Assert.Contains(health.IssueExamples, issue =>
            issue.Code == "BLF_CONTAINER_RESYNCHRONIZATION" &&
            issue.Severity == TraceImportIssueSeverity.Warning);
    }

    [Fact]
    public async Task CorrectBlfObjectAlignmentIsInformationalNotADataLossWarning()
    {
        var file = BuildBlf(LogContainer(
            Concat(CanMessageObject(1, 0x100, 1_000_000, [0x01]),
                   CanMessageObject(1, 0x101, 2_000_000, [0x02])),
            compress: true));
        var (frames, health) = await ReadWithHealthAsync(file);
        Assert.Equal(2, frames.Count);
        Assert.True(health.Completed);
        Assert.Equal(0, health.UnexpectedContainerBytes);
        Assert.False(health.RequiresReview);
    }

    private static async Task<(IReadOnlyList<CanFrame> Frames, TraceImportHealthSnapshot Health)>
        ReadWithHealthAsync(byte[] contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-health-blf-{Guid.NewGuid():N}.blf");
        await File.WriteAllBytesAsync(path, contents);
        try
        {
            var collector = new TraceImportHealth();
            var frames = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path, diagnostics: collector))
                frames.Add(frame);
            return (frames, collector.Snapshot());
        }
        finally { File.Delete(path); }
    }

    private static async Task<List<CanFrame>> ReadAsync(byte[] file)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}.blf");
        await File.WriteAllBytesAsync(path, file);
        try
        {
            var frames = new List<CanFrame>();
            await foreach (var frame in BlfImporter.ReadAsync(path))
            {
                frames.Add(frame);
            }

            return frames;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildBlf(params byte[][] objects)
    {
        using var stream = new MemoryStream();
        var header = new byte[144];
        "LOGG"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 144);
        stream.Write(header);
        foreach (var obj in objects)
        {
            stream.Write(obj);
            var padding = (4 - obj.Length % 4) % 4;
            stream.Write(new byte[padding]);
        }

        return stream.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts)
        {
            stream.Write(part);
            var padding = (4 - part.Length % 4) % 4;
            stream.Write(new byte[padding]);
        }

        return stream.ToArray();
    }

    /// <summary>CAN_MESSAGE (type 1) with a V1 object header, nanosecond timestamps.</summary>
    private static byte[] CanMessageObject(
        ushort channel, uint id, ulong timestampNs, byte[] data, uint timestampFlags = 2)
    {
        var body = new byte[16 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), timestampFlags); // 1=10us, 0/2=ns
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 1);           // headerVersion
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), timestampNs); // timestamp
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(16), channel);
        body[19] = (byte)data.Length;                                          // dlc
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), id);
        data.CopyTo(body.AsSpan(24));

        return WrapObject(1, body, headerSize: 32);
    }

    private static byte[] LogContainer(byte[] contained, bool compress)
    {
        byte[] payload;
        ushort method;
        if (compress)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true))
            {
                zlib.Write(contained);
            }

            payload = compressed.ToArray();
            method = 2;
        }
        else
        {
            payload = contained;
            method = 0;
        }

        var body = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), method);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)contained.Length);
        payload.CopyTo(body.AsSpan(16));
        return WrapObject(10, body);
    }

    private static byte[] WrapObject(uint objectType, byte[] body, ushort headerSize = 16, ushort headerVersion = 1)
    {
        var result = new byte[16 + body.Length];
        "LOBJ"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), headerVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), objectType);
        body.CopyTo(result.AsSpan(16));
        return result;
    }
}
