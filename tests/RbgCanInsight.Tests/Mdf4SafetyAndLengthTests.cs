using System.Buffers.Binary;
using System.Text;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>
/// Synthetic, independently constructed MDF4 block graphs. The oracle uses
/// known time/ID/payload bytes, not the production importer or another parser.
/// </summary>
public sealed class Mdf4SafetyAndLengthTests
{
    [Theory]
    [InlineData("DataLength", 12, 12)]
    [InlineData("DataLength", 24, 24)]
    [InlineData("DataLength", 64, 64)]
    [InlineData("DLC", 9, 12)]
    [InlineData("DLC", 12, 24)]
    [InlineData("DLC", 15, 64)]
    public async Task RawCanFrame_DlcOrByteCountPreservesExactPayloadLength(
        string lengthField, int rawLength, int expectedBytes)
    {
        var path = TempPath();
        File.WriteAllBytes(path, MakeMdf(lengthField, rawLength, 64));
        try
        {
            var frames = await Import(path);
            var frame = Assert.Single(frames);
            Assert.Equal(0x123u, frame.Identifier);
            Assert.Equal(expectedBytes, frame.DataLength);
            Assert.True(frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate));
            Assert.True(frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch));
            Assert.Equal(125_000_000L, frame.TimestampNanoseconds);
            Assert.Equal((byte)0xA5, frame.GetByte(expectedBytes - 1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeclaredPayloadLongerThanAvailableBytes_IsReportedNotShortened()
    {
        var path = TempPath();
        File.WriteAllBytes(path, MakeMdf("DataLength", 24, 12));
        var health = new TraceImportHealth();
        try
        {
            var frames = await Import(path, health);
            Assert.Empty(frames);
            Assert.Contains(health.Snapshot().IssueExamples,
                issue => issue.Code == "MDF_UNDECODABLE_CAN_RECORD");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("DataLength", 65)]
    [InlineData("DLC", 16)]
    public async Task ImpossibleLengthCodes_DoNotBecomeCappedOrInventedFrames(
        string field, int length)
    {
        var path = TempPath();
        File.WriteAllBytes(path, MakeMdf(field, length, 64));
        var health = new TraceImportHealth();
        try
        {
            Assert.Empty(await Import(path, health));
            Assert.Contains(health.Snapshot().IssueExamples,
                issue => issue.Code == "MDF_INVALID_CAN_RECORD");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task HugeNonMdfFile_IsRejectedByHeaderWithoutBufferingIt()
    {
        var path = TempPath();
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                stream.SetLength(300L * 1024 * 1024); // sparse; larger than the former 256 MB cap
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Import(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    public async Task CorruptMdfHeaderBlockBoundaries_FailWithoutUncheckedReads(ulong blockSize)
    {
        var path = TempPath();
        var bytes = MakeMdf("DataLength", 12, 64);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64 + 8), blockSize);
        File.WriteAllBytes(path, bytes);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await Import(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CyclicDataGroupLink_IsRejected()
    {
        var path = TempPath();
        var bytes = MakeMdf("DataLength", 12, 64);
        var hd = 64;
        var dg = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(hd + 24));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(dg + 24), dg);
        File.WriteAllBytes(path, bytes);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Import(path));
        }
        finally { File.Delete(path); }
    }

    private static async Task<List<CanFrame>> Import(string path, TraceImportHealth? health = null)
    {
        var frames = new List<CanFrame>();
        await foreach (var frame in Mdf4Importer.ReadAsync(path, diagnostics: health))
            frames.Add(frame);
        return frames;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"rbg-mf4-synthetic-{Guid.NewGuid():N}.mf4");

    private static byte[] MakeMdf(string lengthName, int codedLength, int dataBytes)
    {
        using var output = new MemoryStream();
        var header = new byte[64];
        Encoding.ASCII.GetBytes("MDF").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("4.10").CopyTo(header, 8);
        output.Write(header);

        static byte[] FieldData(byte channelType, byte syncType, byte dataType,
            uint offset, uint bits)
        {
            var bytes = new byte[12];
            bytes[0] = channelType;
            bytes[1] = syncType;
            bytes[2] = dataType;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), bits);
            return bytes;
        }

        long Block(string kind, int linkCount, byte[] data)
        {
            var offset = output.Position;
            var prefix = new byte[24 + linkCount * 8];
            Encoding.ASCII.GetBytes(kind).CopyTo(prefix, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(prefix.AsSpan(8),
                (ulong)(prefix.Length + data.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(prefix.AsSpan(16), (ulong)linkCount);
            output.Write(prefix);
            output.Write(data);
            return offset;
        }

        void Link(long from, int index, long to)
        {
            var position = output.Position;
            output.Position = from + 24 + 8L * index;
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, to);
            output.Write(bytes);
            output.Position = position;
        }

        var hd = Block("##HD", 1, []);
        var dg = Block("##DG", 3, [0]);
        var cgData = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(cgData.AsSpan(24),
            (uint)(13 + dataBytes + 3)); // 8 time + 4 ID + 1 length + data + FDF + BRS + IDE
        var cg = Block("##CG", 2, cgData);
        var fields = new (string Name, byte Type, byte Sync, byte DataType, uint Offset, uint Bits)[]
        {
            ("Time", 2, 1, 4, 0, 64),
            ("ID", 0, 0, 0, 8, 32),
            (lengthName, 0, 0, 0, 12, 8),
            ("DataBytes", 0, 0, 0, 13, (uint)dataBytes * 8),
            ("FDF", 0, 0, 0, (uint)(13 + dataBytes), 8),
            ("BRS", 0, 0, 0, (uint)(14 + dataBytes), 8),
            ("IDE", 0, 0, 0, (uint)(15 + dataBytes), 8)
        };
        var channels = new List<long>();
        foreach (var field in fields)
        {
            var cn = Block("##CN", 5, FieldData(field.Type, field.Sync, field.DataType,
                field.Offset, field.Bits));
            var name = Block("##TX", 0, Encoding.UTF8.GetBytes(field.Name + "\0"));
            Link(cn, 2, name);
            channels.Add(cn);
        }

        var record = new byte[13 + dataBytes + 3];
        BinaryPrimitives.WriteDoubleLittleEndian(record, 0.125d);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), 0x123);
        record[12] = checked((byte)codedLength);
        for (var i = 0; i < dataBytes; i++) record[13 + i] = 0xA5;
        record[13 + dataBytes] = 1;
        record[14 + dataBytes] = 1;
        record[15 + dataBytes] = 0;
        var dt = Block("##DT", 0, record);

        Link(hd, 0, dg);
        Link(dg, 1, cg);
        Link(dg, 2, dt);
        Link(cg, 1, channels[0]);
        for (var i = 1; i < channels.Count; i++)
            Link(channels[i - 1], 0, channels[i]);
        return output.ToArray();
    }
}
