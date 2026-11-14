using System.Buffers.Binary;
using System.Text;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class Mdf4WriterTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"rbg-mf4-writer-{Guid.NewGuid():N}.mf4");

    private static async Task<List<CanFrame>> Import(string path, TraceImportHealth? health = null)
    {
        var frames = new List<CanFrame>();
        await foreach (var frame in Mdf4Importer.ReadAsync(path, diagnostics: health))
            frames.Add(frame);
        return frames;
    }

    [Fact]
    public async Task RoundTripsClassicExtendedAndFdFramesExactly()
    {
        var path = TempPath();
        try
        {
            var input = new[]
            {
                new CanFrame(0, 0x123, [1, 2, 3], 1_000_000, CanFrameFlags.None),
                new CanFrame(1, 0x18FEF100, [9, 8, 7, 6, 5, 4, 3, 2], 2_000_001, CanFrameFlags.Extended),
                new CanFrame(2, 0x1FFFFFFF, [], 3_123_456_789, CanFrameFlags.Extended),
                new CanFrame(0, 0x7FF, new byte[12], 4_000_000_001,
                    CanFrameFlags.FlexibleDataRate | CanFrameFlags.BitRateSwitch),
                new CanFrame(3, 0x456, [0xAA, 0xBB], 5_000_000_000, CanFrameFlags.FlexibleDataRate),
                new CanFrame(0, 0x1ABCDE0, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(),
                    123_456_789_123, CanFrameFlags.Extended | CanFrameFlags.FlexibleDataRate),
                new CanFrame(0, 0x100, [0xFF], 0, CanFrameFlags.None),
            };
            var result = await Mdf4Writer.WriteAsync(path, input);
            Assert.Equal(input.Length, result.FramesWritten);
            Assert.Equal(0, result.FramesSkipped);

            var output = (await Import(path)).OrderBy(f => f.TimestampNanoseconds).ToList();
            var expected = input.OrderBy(f => f.TimestampNanoseconds).ToList();
            Assert.Equal(expected.Count, output.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Channel, output[i].Channel);
                Assert.Equal(expected[i].Identifier, output[i].Identifier);
                Assert.Equal(expected[i].Flags, output[i].Flags);
                Assert.Equal(expected[i].GetPayload(), output[i].GetPayload());
                // Timestamps are f64 seconds; the writer picks the double that the importer
                // truncates back to the exact nanosecond, so values below ~104 days are exact.
                Assert.Equal(expected[i].TimestampNanoseconds, output[i].TimestampNanoseconds);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SkipsAndCountsRemoteErrorAndUnrepresentableFramesWithoutAlteringTheRest()
    {
        var path = TempPath();
        try
        {
            var input = new[]
            {
                new CanFrame(0, 0x100, [1], 1000),
                new CanFrame(0, 0x101, [], 2000, CanFrameFlags.Remote, declaredDataLength: 4),
                new CanFrame(0, 0x102, [], 3000, CanFrameFlags.Error),
                new CanFrame(0, 0x103, new byte[9], 4000, CanFrameFlags.FlexibleDataRate), // 9 is not an FD length
                new CanFrame(300, 0x104, [1], 5000),
                new CanFrame(0, 0x105, [2], 6000),
            };
            var result = await Mdf4Writer.WriteAsync(path, input);
            Assert.Equal(2, result.FramesWritten);
            Assert.Equal(1, result.RemoteFramesSkipped);
            Assert.Equal(1, result.ErrorFramesSkipped);
            Assert.Equal(1, result.InvalidLengthFramesSkipped);
            Assert.Equal(1, result.ChannelOutOfRangeFramesSkipped);
            Assert.Equal(4, result.FramesSkipped);

            var output = await Import(path);
            Assert.Equal(new uint[] { 0x100, 0x105 }, output.Select(f => f.Identifier).ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmptyInputProducesAnImportableFileWithZeroFrames()
    {
        var path = TempPath();
        try
        {
            var result = await Mdf4Writer.WriteAsync(path, []);
            Assert.Equal(0, result.FramesWritten);
            Assert.Empty(await Import(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WritesValidMdf41BlockStructure()
    {
        var path = TempPath();
        try
        {
            await Mdf4Writer.WriteAsync(path, [new CanFrame(0, 0x123, [1, 2, 3], 1_000_000)]);
            var file = await File.ReadAllBytesAsync(path);
            Assert.Equal("MDF     ", Encoding.ASCII.GetString(file, 0, 8));
            Assert.Equal("4.10    ", Encoding.ASCII.GetString(file, 8, 8));
            Assert.Equal(410, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(28)));
            Assert.Equal("##HD", Encoding.ASCII.GetString(file, 64, 4));

            // Every block is 8-byte aligned, tiles the file exactly and links only point at block starts.
            var starts = new HashSet<long>();
            var blocks = new List<(long Offset, long Length, long Links)>();
            long position = 64;
            while (position < file.Length)
            {
                Assert.Equal(0, position % 8);
                var id = Encoding.ASCII.GetString(file, (int)position, 4);
                Assert.StartsWith("##", id);
                var length = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan((int)position + 8));
                var links = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan((int)position + 16));
                Assert.True(length >= 24 + 8 * links);
                if (id != "##DT") Assert.Equal(0, length % 8);
                starts.Add(position);
                blocks.Add((position, length, links));
                position += length;
            }
            Assert.Equal(file.Length, position);
            foreach (var (offset, _, links) in blocks)
                for (var i = 0; i < links; i++)
                {
                    var target = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan((int)(offset + 24 + 8 * i)));
                    Assert.True(target == 0 || starts.Contains(target), $"dangling link at {offset}[{i}]");
                }
            Assert.Contains(blocks, b => Encoding.ASCII.GetString(file, (int)b.Offset, 4) == "##FH");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        var path = TempPath();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Mdf4Writer.WriteAsync(path, [new CanFrame(0, 1, [1], 0)], cts.Token));
        File.Delete(path);
    }
}
