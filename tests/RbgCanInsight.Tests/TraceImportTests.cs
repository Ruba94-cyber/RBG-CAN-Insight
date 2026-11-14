using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceImportTests
{
    private static async Task<List<CanFrame>> ImportAsync(string extension, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(path, content);
        try
        {
            var frames = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path))
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

    [Fact]
    public async Task ImportsVectorAsc()
    {
        var frames = await ImportAsync(".asc",
            """
            date Mon Jan 05 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.001000 1 100 Rx d 8 01 02 03 04 05 06 07 08
            0.002000 1 18FEF100x Rx d 3 AA BB CC
            """);

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x100u, frames[0].Identifier);
        Assert.Equal(1_000_000, frames[0].TimestampNanoseconds);
        Assert.Equal(8, frames[0].DataLength);
        Assert.True(frames[1].IsExtended);
        Assert.Equal(0x18FEF100u, frames[1].Identifier);
    }

    [Fact]
    public async Task ImportsCandumpLog()
    {
        var frames = await ImportAsync(".log",
            """
            (1600000000.123456) can0 123#DEADBEEF
            (1600000000.223456) can1 18FEF100#0102
            """);

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x123u, frames[0].Identifier);
        Assert.Equal(4, frames[0].DataLength);
        Assert.Equal(1, frames[1].Channel);
    }

    [Fact]
    public async Task ImportsCsv()
    {
        var frames = await ImportAsync(".csv",
            """
            time,channel,id,data
            0.5,0,0x1A0,11 22 33
            0.6,1,2C4,44
            """);

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x1A0u, frames[0].Identifier);
        Assert.Equal(500_000_000, frames[0].TimestampNanoseconds);
        Assert.Equal(0x2C4u, frames[1].Identifier);
    }

    [Fact]
    public async Task ImportsPcanTrcV1()
    {
        var frames = await ImportAsync(".trc",
            """
            ;$FILEVERSION=1.1
            ;   Start time: 05/01/2026
            ;---+--   ----+----  --+--  ----+---  +  -+ -- -- -- -- -- -- --
                 1)      1059.9  Rx         0100  8  01 02 03 04 05 06 07 08
                 2)      1060.9  Rx         0200  2  AA BB
            """);

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x100u, frames[0].Identifier);
        Assert.Equal(8, frames[0].DataLength);
        Assert.Equal(1_059_900_000, frames[0].TimestampNanoseconds);
    }

    [Fact]
    public async Task ImportsPcanTrcV2()
    {
        var frames = await ImportAsync(".trc",
            """
            ;$FILEVERSION=2.0
            ;$STARTTIME=45000.0
             1 1059.900 DT 0100 Rx 8 01 02 03 04 05 06 07 08
             2 1060.900 DT 18FEF100 Rx 2 AA BB
            """);

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x100u, frames[0].Identifier);
        Assert.True(frames[1].IsExtended);
    }

    [Fact]
    public async Task ExportRoundTripsThroughAsc()
    {
        var source = new List<CanFrame>
        {
            new(0, 0x1A0, [0x11, 0x22], 1_500_000, CanFrameFlags.None, 1),
            new(1, 0x18FEF100, [0xAA], 2_500_000, CanFrameFlags.Extended, 2)
        };
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}.asc");
        try
        {
            await TraceExporter.WriteAscAsync(path, source);
            var frames = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path))
            {
                frames.Add(frame);
            }

            Assert.Equal(2, frames.Count);
            Assert.Equal(source[0].Identifier, frames[0].Identifier);
            Assert.Equal(source[1].Identifier, frames[1].Identifier);
            Assert.True(frames[1].IsExtended);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportRoundTripsThroughTrc()
    {
        var source = new List<CanFrame>
        {
            new(0, 0x1A0, [0x11, 0x22, 0x33, 0x44], 1_500_000, CanFrameFlags.None, 1),
            new(0, 0x18FEF100, [0xAA, 0xBB], 2_500_000, CanFrameFlags.Extended, 2)
        };
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}.trc");
        try
        {
            await TraceExporter.WriteTrcAsync(path, source);
            var frames = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path))
            {
                frames.Add(frame);
            }

            Assert.Equal(2, frames.Count);
            Assert.Equal(source[0].Identifier, frames[0].Identifier);
            Assert.Equal(source[0].GetPayload(), frames[0].GetPayload());
            Assert.Equal(source[1].Identifier, frames[1].Identifier);
            Assert.True(frames[1].IsExtended);
            Assert.Equal(source[1].GetPayload(), frames[1].GetPayload());
        }
        finally
        {
            File.Delete(path);
        }
    }



    [Fact]
    public async Task RemoteRequestedDlcSurvivesBlfAscAndTrcWithoutInventingPayload()
    {
        // RTR carries no payload: requested DLC is wire metadata, not eight zeros.
        var source = new CanFrame(0, 0x456, [], 2_000_000_000,
            CanFrameFlags.Remote, declaredDataLength: 8);
        Assert.Equal(0, source.DataLength);
        Assert.Equal(8, source.DeclaredDataLength);
        Assert.Empty(source.GetPayload());
        Assert.Throws<ArgumentException>(() => new CanFrame(0, 0x456, [0x00],
            0, CanFrameFlags.Remote, declaredDataLength: 8));

        foreach (var extension in new[] { ".blf", ".asc", ".trc" })
        {
            var path = Path.Combine(Path.GetTempPath(),
                $"rbg-rtr-{Guid.NewGuid():N}{extension}");
            try
            {
                switch (extension)
                {
                    case ".blf": await TraceExporter.WriteBlfAsync(path, [source]); break;
                    case ".asc": await TraceExporter.WriteAscAsync(path, [source]); break;
                    default: await TraceExporter.WriteTrcAsync(path, [source]); break;
                }
                var imported = new List<CanFrame>();
                await foreach (var frame in TraceImporter.ReadAsync(path))
                    imported.Add(frame);
                var actual = Assert.Single(imported);
                Assert.True(actual.Flags.HasFlag(CanFrameFlags.Remote));
                Assert.Equal(source.Identifier, actual.Identifier);
                Assert.Equal(8, actual.DeclaredDataLength);
                Assert.Equal(0, actual.DataLength);
                Assert.Empty(actual.GetPayload());
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ExportedBlfPreservesCanFdDlcErrorAndRemoteFlags()
    {
        var source = new[]
        {
            new CanFrame(0, 0x123, Enumerable.Range(0, 20).Select(i => (byte)i).ToArray(),
                1_000_000, CanFrameFlags.FlexibleDataRate | CanFrameFlags.BitRateSwitch),
            new CanFrame(1, 0x456, [], 2_000_000, CanFrameFlags.Remote),
            new CanFrame(2, 0x123, [0xDE, 0xAD], 3_000_000, CanFrameFlags.Error)
        };
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}.blf");
        try
        {
            await TraceExporter.WriteBlfAsync(path, source);
            var bytes = await File.ReadAllBytesAsync(path);
            // LOG_CONTAINER at 144, its 32-byte header, then first V1 CAN_FD_MESSAGE:
            // the DLC byte is at offset 16 + 16 + 3 inside that object.
            Assert.Equal(11, bytes[144 + 32 + 16 + 16 + 3]);

            var imported = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path))
                imported.Add(frame);
            Assert.Equal(source.Length, imported.Count);
            Assert.Equal(20, imported[0].DataLength);
            Assert.True(imported[0].Flags.HasFlag(CanFrameFlags.FlexibleDataRate));
            Assert.True(imported[0].Flags.HasFlag(CanFrameFlags.BitRateSwitch));
            Assert.True(imported[1].Flags.HasFlag(CanFrameFlags.Remote));
            Assert.True(imported[2].IsError);
            Assert.Equal(0x123u, imported[2].Identifier);
            Assert.Equal(new byte[] { 0xDE, 0xAD }, imported[2].GetPayload());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportedBlfUsesActualLogContainersAndFileHeaderCounts()
    {
        // A BLF reader external to RBG must be able to find CAN data in LOBJ
        // type 10 containers, not as loose top-level CAN objects.
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}.blf");
        var frames = Enumerable.Range(0, 3200)
            .Select(i => new CanFrame(0, 0x123, [(byte)(i & 255)], i * 100_000L))
            .ToArray();
        try
        {
            await TraceExporter.WriteBlfAsync(path, frames);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(0x47474F4Cu,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes));
            Assert.Equal((ulong)bytes.Length,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16)));
            Assert.Equal(3200u,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)));

            var offset = 144;
            var containers = 0;
            var decodedBytes = 0;
            while (offset < bytes.Length)
            {
                Assert.Equal(0x4A424F4Cu,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
                Assert.Equal(16,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4)));
                var objectSize = (int)System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(bytes.AsSpan(offset + 8));
                Assert.Equal(10u, System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(bytes.AsSpan(offset + 12)));
                Assert.Equal(0,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 16)));
                var payloadLength = (int)System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(bytes.AsSpan(offset + 24));
                Assert.Equal(objectSize - 32, payloadLength);
                decodedBytes += payloadLength;
                containers++;
                offset += objectSize + objectSize % 4;
            }
            Assert.Equal(bytes.Length, offset);
            Assert.True(containers >= 2);
            Assert.Equal(frames.Length * 48, decodedBytes);

            var imported = new List<CanFrame>();
            await foreach (var frame in BlfImporter.ReadAsync(path))
                imported.Add(frame);
            Assert.Equal(frames.Length, imported.Count);
            Assert.Equal(frames[^1].GetPayload(), imported[^1].GetPayload());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportRoundTripsThroughBlf()
    {
        var source = new List<CanFrame>
        {
            new(0, 0x1A0, [0x11, 0x22, 0x33], 1_500_000, CanFrameFlags.None, 1),
            new(1, 0x18FEF100, [0xAA], 2_500_000, CanFrameFlags.Extended, 2),
            new(0, 0x200, Enumerable.Range(0, 20).Select(i => (byte)i).ToArray(), 3_500_000, CanFrameFlags.FlexibleDataRate, 3)
        };
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}.blf");
        try
        {
            await TraceExporter.WriteBlfAsync(path, source);
            var frames = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path))
            {
                frames.Add(frame);
            }

            Assert.Equal(3, frames.Count);
            Assert.Equal(source[0].Identifier, frames[0].Identifier);
            Assert.Equal(source[0].Channel, frames[0].Channel);
            Assert.Equal(source[0].GetPayload(), frames[0].GetPayload());
            Assert.Equal(source[0].TimestampNanoseconds, frames[0].TimestampNanoseconds);

            Assert.Equal(source[1].Identifier, frames[1].Identifier);
            Assert.Equal(source[1].Channel, frames[1].Channel);
            Assert.True(frames[1].IsExtended);
            Assert.Equal(source[1].GetPayload(), frames[1].GetPayload());

            Assert.Equal(source[2].Identifier, frames[2].Identifier);
            Assert.Equal(source[2].GetPayload(), frames[2].GetPayload());
            Assert.True(frames[2].Flags.HasFlag(CanFrameFlags.FlexibleDataRate));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
