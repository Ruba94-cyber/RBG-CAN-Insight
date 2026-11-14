using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>The MDF4 reader streams blocks through a seekable stream; there is no file-size cap.
/// A small configurable block budget stands in for "larger than 256 MB" in these tests.</summary>
public sealed class Mdf4StreamingTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"rbg-mf4-stream-{Guid.NewGuid():N}.mf4");

    private static List<CanFrame> Make(int count) =>
        Enumerable.Range(0, count).Select(i => new CanFrame(i % 2, (uint)(0x100 + i % 50),
            [(byte)i, (byte)(i >> 8), 3, 4, 5, 6, 7, 8], 1_000_000L + i * 1_000L, CanFrameFlags.None)).ToList();

    private static async Task<(List<CanFrame> Frames, Mdf4ReadStats Stats)> Import(string path, Mdf4ReadOptions o)
    {
        var stats = new Mdf4ReadStats();
        var frames = new List<CanFrame>();
        await foreach (var f in Mdf4Importer.ReadAsync(path, options: o with { Stats = stats })) frames.Add(f);
        return (frames, stats);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FileFarLargerThanBlockBudgetRoundTripsWithBoundedMemory(bool compress, bool transpose)
    {
        var path = TempPath();
        try
        {
            var input = Make(40_000); // ~1.2 MB of records vs a 64 KB budget
            await Mdf4Writer.WriteAsync(path, input, options: new Mdf4WriteOptions(
                MaxRecordsPerDataBlock: 1_000, Compress: compress, TransposeCompression: transpose));
            var budget = new Mdf4ReadOptions { MaxBlockBytes = 64 * 1024, ChunkBytes = 4096 };
            if (!compress) Assert.True(new FileInfo(path).Length > budget.MaxBlockBytes * 10);
            var (frames, stats) = await Import(path, budget);
            Assert.Equal(input.Count, frames.Count);
            for (var i = 0; i < input.Count; i += 997)
            {
                Assert.Equal(input[i].Identifier, frames[i].Identifier);
                Assert.Equal(input[i].TimestampNanoseconds, frames[i].TimestampNanoseconds);
                Assert.Equal(input[i].GetPayload(), frames[i].GetPayload());
            }
            Assert.True(stats.DataBlocks >= 40);
            Assert.True(stats.PeakBufferedBytes <= budget.MaxBlockBytes, $"peak {stats.PeakBufferedBytes}");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RecordsSpanningChunkBoundariesAreReassembled()
    {
        var path = TempPath();
        try
        {
            var input = Make(500);
            await Mdf4Writer.WriteAsync(path, input);
            var (frames, _) = await Import(path, new Mdf4ReadOptions { ChunkBytes = 101});
            Assert.Equal(input.Select(f => f.Identifier), frames.Select(f => f.Identifier));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CompressedBlockLargerThanBudgetIsRejected()
    {
        var path = TempPath();
        try
        {
            await Mdf4Writer.WriteAsync(path, Make(5_000), options: new Mdf4WriteOptions(5_000, Compress: true));
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await Import(path, new Mdf4ReadOptions { MaxBlockBytes = 8 * 1024 }));
        }
        finally { File.Delete(path); }
    }
}
