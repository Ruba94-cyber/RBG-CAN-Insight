using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Regressions for defects found by the 2026-11-11 importer audit. Every expected value
/// is derived from the input text or bytes, never from the importer's own output.</summary>
public sealed class TraceImportAuditRegressionTests
{
    private static string Tmp(string ext) => Path.Combine(Path.GetTempPath(), $"rbg-audit-{Guid.NewGuid():N}{ext}");

    private static async Task<(List<CanFrame> Frames, TraceImportHealthSnapshot Health)> ImportAsync(string path)
    {
        var health = new TraceImportHealth();
        var frames = new List<CanFrame>();
        await foreach (var frame in TraceImporter.ReadAsync(path, CancellationToken.None, health))
            frames.Add(frame);
        return (frames, health.Snapshot());
    }

    [Fact]
    public void DiskBackedStoreKeepsRequestedLengthOfRemoteFrames()
    {
        var path = Tmp(".frames");
        try
        {
            using var writer = new DiskBackedCanFrameStoreWriter(path);
            writer.Append(new CanFrame(0, 0x123, [], 1_000, CanFrameFlags.Remote, 0, declaredDataLength: 8));
            writer.Append(new CanFrame(0, 0x124, [1, 2, 3], 2_000, CanFrameFlags.None, 1));
            using var store = writer.CompleteAndOpenForReading(deleteFileOnDispose: false);

            Assert.Equal(8, store[0].DeclaredDataLength);
            Assert.Equal(0, store[0].DataLength);
            Assert.True(store[0].Flags.HasFlag(CanFrameFlags.Remote));
            Assert.Equal(3, store[1].DeclaredDataLength);
            Assert.Equal(new byte[] { 1, 2, 3 }, store[1].GetPayload());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CandumpLogKeepsFdPayloadRemoteRequestsAndIdentifierWidth()
    {
        var path = Tmp(".log");
        await File.WriteAllTextAsync(path,
            "(1612345678.123456) can0 123#1122\n" +
            "(1612345679.000000) can0 7FF##1001122334455667788990A0B\n" +
            "(1612345680.000000) can0 123#R\n" +
            "(1612345681.000000) can1 00000123#AA\n" +
            "(1612345682.000000) can0 12345678#R4\n" +
            "(1612345683.000000) can9 711#0322 T\n" +
            "(1612345684.000000) can9 719#0562 R\n");
        try
        {
            var (frames, health) = await ImportAsync(path);

            Assert.Equal(7, frames.Count);
            // Direction suffix written by newer can-utils: T = transmitted by the logger.
            Assert.Equal(CanFrameFlags.Transmit, frames[5].Flags);
            Assert.Equal(new byte[] { 0x03, 0x22 }, frames[5].GetPayload());
            Assert.Equal(CanFrameFlags.None, frames[6].Flags);
            Assert.Equal(9, frames[6].Channel);
            Assert.False(health.RequiresReview);

            // Epoch seconds with microseconds must convert exactly, not through a double.
            Assert.Equal(1_612_345_678_123_456_000L, frames[0].TimestampNanoseconds);
            Assert.Equal(new byte[] { 0x11, 0x22 }, frames[0].GetPayload());
            Assert.Equal(CanFrameFlags.None, frames[0].Flags);

            // "##1" = CAN FD with bit-rate switch; the 12 bytes that follow are the payload.
            Assert.Equal(0x7FFu, frames[1].Identifier);
            Assert.Equal(CanFrameFlags.FlexibleDataRate | CanFrameFlags.BitRateSwitch, frames[1].Flags);
            Assert.Equal(
                new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0x0A, 0x0B },
                frames[1].GetPayload());

            Assert.Equal(CanFrameFlags.Remote, frames[2].Flags);
            Assert.Equal(0, frames[2].DataLength);

            // Eight identifier digits mean a 29-bit frame even when the value fits in 11 bits.
            Assert.Equal(0x123u, frames[3].Identifier);
            Assert.True(frames[3].IsExtended);
            Assert.Equal(1, frames[3].Channel);

            Assert.Equal(CanFrameFlags.Extended | CanFrameFlags.Remote, frames[4].Flags);
            Assert.Equal(4, frames[4].DeclaredDataLength);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CandumpLineWithTrailingGarbageIsRejectedNotImportedAsEmptyFrame()
    {
        var path = Tmp(".log");
        await File.WriteAllTextAsync(path,
            "(1.000000) can0 123#1122\n" +
            "(2.000000) can0 123#11ZZ\n");
        try
        {
            var (frames, health) = await ImportAsync(path);

            Assert.Equal(1_000_000_000L, Assert.Single(frames).TimestampNanoseconds);
            Assert.True(health.RequiresReview);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExportedCsvReimportsCleanlyWithExactTimeAndRemoteLength()
    {
        var path = Tmp(".csv");
        CanFrame[] source =
        [
            new(0, 0x100, [0xDE, 0xAD], 1_612_345_678_123_456_789L, CanFrameFlags.None, 0),
            new(1, 0x200, [], 1_612_345_679_000_000_001L, CanFrameFlags.Remote, 1, declaredDataLength: 4)
        ];
        try
        {
            await TraceExporter.WriteCsvAsync(path, source);
            var (frames, health) = await ImportAsync(path);

            // The exporter's own header line is not a rejected record.
            Assert.False(health.RequiresReview);
            Assert.Equal(2, frames.Count);
            Assert.Equal(source[0].TimestampNanoseconds, frames[0].TimestampNanoseconds);
            Assert.Equal(source[0].GetPayload(), frames[0].GetPayload());
            Assert.Equal(source[1].TimestampNanoseconds, frames[1].TimestampNanoseconds);
            Assert.True(frames[1].Flags.HasFlag(CanFrameFlags.Remote));
            Assert.Equal(4, frames[1].DeclaredDataLength);
            Assert.Equal(1, frames[1].Channel);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TrcFdFrameWithBitRateSwitchKeepsTheBrsFlag()
    {
        var path = Tmp(".trc");
        await File.WriteAllTextAsync(path,
            ";$FILEVERSION=2.1\n" +
            ";$STARTTIME=46000.5\n" +
            ";$COLUMNS=N,O,T,B,I,d,R,L,D\n" +
            "      1         1.0000 FD  1     0123 Rx -  9    01 02 03 04 05 06 07 08 09 0A 0B 0C\n" +
            "      2         2.0000 FB  1     0124 Rx -  9    01 02 03 04 05 06 07 08 09 0A 0B 0C\n");
        try
        {
            var (frames, _) = await ImportAsync(path);

            Assert.Equal(2, frames.Count);
            Assert.True(frames[0].Flags.HasFlag(CanFrameFlags.FlexibleDataRate));
            Assert.False(frames[0].Flags.HasFlag(CanFrameFlags.BitRateSwitch));
            Assert.True(frames[1].Flags.HasFlag(CanFrameFlags.FlexibleDataRate));
            Assert.True(frames[1].Flags.HasFlag(CanFrameFlags.BitRateSwitch));
            Assert.Equal(12, frames[1].DataLength); // FD DLC 9 = 12 bytes
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task EmptyOrTinyBlfIsReportedAsInvalidDataNotAsAnInternalError(int size)
    {
        var path = Tmp(".blf");
        await File.WriteAllBytesAsync(path, new byte[size]);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(path));
            Assert.Contains("BLF", error.Message);
        }
        finally { File.Delete(path); }
    }
}
