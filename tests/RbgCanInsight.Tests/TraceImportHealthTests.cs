using System.Buffers.Binary;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceImportHealthTests
{
    private static async Task<(IReadOnlyList<CanFrame> Frames, TraceImportHealthSnapshot Health)> Import(
        string path)
    {
        var collector = new TraceImportHealth();
        var frames = new List<CanFrame>();
        await foreach (var frame in TraceImporter.ReadAsync(path, diagnostics: collector))
            frames.Add(frame);
        return (frames, collector.Snapshot());
    }

    private static async Task WithText(string extension, string text, Func<string, Task> action)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"rbg-import-health-{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(path, text);
        try { await action(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AscHealthCountsMetadataRejectedTruncatedRecordsAndRealChannels()
    {
        await WithText(".asc",
            """
            date Wed Nov 04 09:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.001000 1 100 Rx d 2 11 22
            0.002000 2 18FEF100x Rx d 2 0A 0B
            0.003000 1 100 Rx d 8 11 22
            0.004000 1 100 Rx d 2 11 33
            """, async path =>
        {
            var (frames, health) = await Import(path);
            Assert.Equal(3, frames.Count);
            Assert.True(health.Completed);
            Assert.Equal("ASC", health.Format);
            Assert.Equal(7, health.PhysicalLines);
            Assert.Equal(3, health.MetadataLines);
            Assert.Equal(1, health.RejectedRecords);
            Assert.Equal(2, health.Channels.Count);
            Assert.Equal(2, health.Channels.Single(row => row.Channel == 0).Frames);
            Assert.Equal(1, health.Channels.Single(row => row.Channel == 1).Frames);
            Assert.True(health.RequiresReview);
            Assert.Contains(health.IssueExamples, issue =>
                issue.LineNumber == 6 && issue.Code == "MALFORMED_RECORD");
        });
    }

    [Fact]
    public async Task BadCsvPayloadIsCountedAndDoesNotSuppressLaterValidFrames()
    {
        await WithText(".csv",
            """
            time,channel,id,data
            0.1,0,0x123,01 02
            0.2,0,0x123,not-hex
            0.3,0,0x123,03 04
            """, async path =>
        {
            var (frames, health) = await Import(path);
            Assert.Equal(2, frames.Count);
            Assert.Equal(1, health.MetadataLines);
            Assert.Equal(1, health.RejectedRecords);
            Assert.True(health.Completed);
            Assert.Contains(health.IssueExamples, issue => issue.LineNumber == 3);
        });
    }

    [Fact]
    public async Task TrcTracksMalformedDataAndPreservesGenuineRemoteFrame()
    {
        await WithText(".trc",
            """
            ;$FILEVERSION=1.1
            1) 100.0 Rx 0100 2 AA BB
            2) 200.0 Rx 0100 4 11
            3) 300.0 RR 0100 8
            """, async path =>
        {
            var (frames, health) = await Import(path);
            Assert.Equal(2, frames.Count);
            Assert.True(frames[1].Flags.HasFlag(CanFrameFlags.Remote));
            Assert.Empty(frames[1].GetPayload());
            Assert.Equal(1, health.Channels.Single().RemoteFrames);
            Assert.Equal(1, health.RejectedRecords);
            Assert.Equal(1, health.MetadataLines);
        });
    }

    [Fact]
    public async Task OutOfOrderFileTimestampsRemainVisibleBeforeSort()
    {
        await WithText(".asc",
            """
            0.002000 1 100 Rx d 1 11
            0.001000 2 200 Rx d 1 22
            """, async path =>
        {
            var (_, health) = await Import(path);
            Assert.Equal(1, health.OutOfOrderTimestamps);
            Assert.Equal(1, health.Channels.Single(row => row.Channel == 1).OutOfOrderTimestamps);
            Assert.Contains(health.IssueExamples, issue => issue.Code == "TIMESTAMP_ORDER");
            Assert.True(health.Completed);
        });
    }

    [Fact]
    public async Task BlfReportsUnsupportedObjectWithoutLabellingItCorrupt()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"rbg-blf-import-health-{Guid.NewGuid():N}.blf");
        try
        {
            await TraceExporter.WriteBlfAsync(path,
                [new CanFrame(0, 0x123, [0x11, 0x22], 1_000_000)]);
            var clean = await File.ReadAllBytesAsync(path);
            var unknown = new byte[20]; // 16-byte LOBJ header, body + 4-byte aligned tail.
            "LOBJ"u8.CopyTo(unknown);
            BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(4), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(8), 17);
            BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(12), 999);
            unknown[16] = 0xAB;
            var modified = new byte[clean.Length + unknown.Length];
            Buffer.BlockCopy(clean, 0, modified, 0, clean.Length);
            Buffer.BlockCopy(unknown, 0, modified, clean.Length, unknown.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(modified.AsSpan(16), (ulong)modified.Length);
            await File.WriteAllBytesAsync(path, modified);

            var (frames, health) = await Import(path);
            Assert.Single(frames);
            Assert.True(health.Completed);
            Assert.Equal(1, health.UnsupportedObjectRecords);
            Assert.False(health.RequiresReview);
            Assert.True(health.BlfContainers > 0);
            Assert.Contains(health.IssueExamples, issue =>
                issue.Code == "UNSUPPORTED_BLF_OBJECT" &&
                issue.Severity == TraceImportIssueSeverity.Information);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BlfHeaderFileSizeMismatchRequiresReviewEvenWhenCanObjectsDecode()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"rbg-blf-import-health-{Guid.NewGuid():N}.blf");
        try
        {
            await TraceExporter.WriteBlfAsync(path,
                [new CanFrame(0, 0x123, [0x11], 1_000_000)]);
            var file = await File.ReadAllBytesAsync(path);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(16), (ulong)(file.Length + 100));
            await File.WriteAllBytesAsync(path, file);
            var (frames, health) = await Import(path);
            Assert.Single(frames);
            Assert.True(health.Completed);
            Assert.True(health.RequiresReview);
            Assert.Contains(health.IssueExamples, issue =>
                issue.Code == "BLF_DECLARED_FILE_SIZE" &&
                issue.Severity == TraceImportIssueSeverity.Warning);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TruncatedBlfCannotBeReportedAsCompleted()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"rbg-blf-import-health-{Guid.NewGuid():N}.blf");
        try
        {
            await TraceExporter.WriteBlfAsync(path,
                [new CanFrame(0, 0x123, [0x11, 0x22], 1_000_000)]);
            var clean = await File.ReadAllBytesAsync(path);
            await File.WriteAllBytesAsync(path, clean[..^12]);
            var collector = new TraceImportHealth();
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (var _ in TraceImporter.ReadAsync(path, diagnostics: collector)) { }
            });
            var health = collector.Snapshot();
            Assert.False(health.Completed);
            Assert.True(health.RequiresReview);
            Assert.NotNull(health.FailureMessage);
            Assert.Contains(health.IssueExamples, issue => issue.Code == "IMPORT_FAILED");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportHealthIssueExamplesAreBoundedAndContainNoInputPayload()
    {
        var rows = Enumerable.Range(0, 300).Select(_ =>
            "1.0,0,0x123,super-secret-payload").ToArray();
        await WithText(".csv", string.Join("\n", rows), async path =>
        {
            var (_, health) = await Import(path);
            Assert.Equal(300, health.RejectedRecords);
            Assert.True(health.IssueExamples.Count <= 64);
            Assert.DoesNotContain("super-secret-payload",
                System.Text.Json.JsonSerializer.Serialize(health), StringComparison.Ordinal);
        });
    }
}
