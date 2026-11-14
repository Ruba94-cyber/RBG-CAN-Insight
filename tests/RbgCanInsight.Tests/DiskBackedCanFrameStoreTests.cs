using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiskBackedCanFrameStoreTests
{
    [Fact]
    public void RoundTripsVariedFramesExactly()
    {
        var source = new[]
        {
            new CanFrame(0, 0x100, [0x01, 0x02, 0x03], 0, CanFrameFlags.None, 1),
            new CanFrame(1, 0x18FEF100, [0xAA, 0xBB], 1_000_000, CanFrameFlags.Extended, 2),
            new CanFrame(0, 0x7FF, [], 2_000_000, CanFrameFlags.Remote, 3, declaredDataLength: 8),
            new CanFrame(2, 0x1FFFFFFF, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(),
                3_000_000, CanFrameFlags.Extended | CanFrameFlags.FlexibleDataRate | CanFrameFlags.BitRateSwitch, 4),
            new CanFrame(0, 0x7E8, [0x00], long.MaxValue / 2, CanFrameFlags.Error, 5)
        };

        var path = TempPath();
        try
        {
            using (var writer = new DiskBackedCanFrameStoreWriter(path))
            {
                foreach (var frame in source)
                {
                    writer.Append(frame);
                }

                using var store = writer.CompleteAndOpenForReading(deleteFileOnDispose: false);

                Assert.Equal(source.Length, store.Count);
                for (var i = 0; i < source.Length; i++)
                {
                    Assert.Equal(source[i].Channel, store[i].Channel);
                    Assert.Equal(source[i].Identifier, store[i].Identifier);
                    Assert.Equal(source[i].IsExtended, store[i].IsExtended);
                    Assert.Equal(source[i].TimestampNanoseconds, store[i].TimestampNanoseconds);
                    Assert.Equal(source[i].Flags, store[i].Flags);
                    Assert.Equal(source[i].DataLength, store[i].DataLength);
                    Assert.Equal(source[i].DeclaredDataLength, store[i].DeclaredDataLength);
                    Assert.Equal(source[i].GetPayload(), store[i].GetPayload());
                    Assert.Equal(source[i].Sequence, store[i].Sequence);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SupportsRandomAccessNotJustSequential()
    {
        var frames = Enumerable.Range(0, 500)
            .Select(i => new CanFrame(i % 3, (uint)(0x100 + i), [(byte)i, (byte)(i >> 8)], i * 1000L))
            .ToArray();

        var path = TempPath();
        try
        {
            using var writer = new DiskBackedCanFrameStoreWriter(path);
            foreach (var frame in frames)
            {
                writer.Append(frame);
            }

            using var store = writer.CompleteAndOpenForReading(deleteFileOnDispose: false);

            // Access out of order, including repeats, to prove there is no sequential-only assumption.
            foreach (var index in new[] { 499, 0, 250, 250, 17, 498 })
            {
                Assert.Equal(frames[index].Identifier, store[index].Identifier);
                Assert.Equal(frames[index].TimestampNanoseconds, store[index].TimestampNanoseconds);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThrowsForOutOfRangeIndex()
    {
        var path = TempPath();
        try
        {
            using var writer = new DiskBackedCanFrameStoreWriter(path);
            writer.Append(new CanFrame(0, 0x100, [0x01], 0));
            using var store = writer.CompleteAndOpenForReading(deleteFileOnDispose: false);

            Assert.Throws<ArgumentOutOfRangeException>(() => store[1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => store[-1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeletesTheBackingFileOnDisposeWhenRequested()
    {
        var path = TempPath();
        using (var writer = new DiskBackedCanFrameStoreWriter(path))
        {
            writer.Append(new CanFrame(0, 0x100, [0x01], 0));
            using (writer.CompleteAndOpenForReading(deleteFileOnDispose: true))
            {
                Assert.True(File.Exists(path));
            }
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void HandlesA100000FrameStoreCorrectlyAtBothEnds()
    {
        const int count = 100_000;
        var path = TempPath();
        try
        {
            using var writer = new DiskBackedCanFrameStoreWriter(path);
            for (var i = 0; i < count; i++)
            {
                writer.Append(new CanFrame(i % 4, (uint)(i % 0x7FF), [(byte)i], i * 100L, CanFrameFlags.None, i));
            }

            using var store = writer.CompleteAndOpenForReading(deleteFileOnDispose: false);

            Assert.Equal(count, store.Count);
            Assert.Equal(0, store[0].Sequence);
            Assert.Equal(count - 1, store[count - 1].Sequence);
            Assert.Equal((count / 2) * 100L, store[count / 2].TimestampNanoseconds);

            // IReadOnlyList<CanFrame> consumers (TraceStatistics, SignalTimelineBuilder, ...)
            // enumerate the whole store; confirm that path visits every record exactly once, too.
            var enumeratedCount = store.Count();
            Assert.Equal(count, enumeratedCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsPartialAndZeroLengthStoreInsteadOfSilentlyIgnoringTrailingBytes()
    {
        var path = TempPath();
        try
        {
            File.WriteAllBytes(path, new byte[DiskBackedCanFrameStore.RecordSize + 1]);
            Assert.Throws<InvalidDataException>(() =>
                DiskBackedCanFrameStore.Open(path, deleteFileOnDispose: false));
            File.WriteAllBytes(path, []);
            Assert.Throws<InvalidDataException>(() =>
                DiskBackedCanFrameStore.Open(path, deleteFileOnDispose: false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsInvalidRawDataLengthOnRandomAccess()
    {
        var path = TempPath();
        try
        {
            var record = new byte[DiskBackedCanFrameStore.RecordSize];
            record[17] = 65;
            File.WriteAllBytes(path, record);
            using var store = DiskBackedCanFrameStore.Open(path, deleteFileOnDispose: false);
            Assert.Throws<InvalidDataException>(() => store[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnfinishedDiskSnapshotIsRemovedOnWriterDispose()
    {
        var path = TempPath();
        using (var writer = new DiskBackedCanFrameStoreWriter(path))
        {
            writer.Append(new CanFrame(0, 0x123, [0xAA], 1_000_000));
            Assert.True(File.Exists(path));
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void OptInPreservesUnfinishedStoreForOfflineForensics()
    {
        var path = TempPath();
        try
        {
            using (var writer = new DiskBackedCanFrameStoreWriter(
                       path, deleteIncompleteFileOnDispose: false))
                writer.Append(new CanFrame(0, 0x123, [0xBB], 2_000_000));
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WriterRejectsAppendAfterComplete()
    {
        var path = TempPath();
        try
        {
            using var writer = new DiskBackedCanFrameStoreWriter(path);
            writer.Append(new CanFrame(0, 0x123, [1], 0));
            using var store = writer.CompleteAndOpenForReading(deleteFileOnDispose: false);
            Assert.Throws<InvalidOperationException>(() =>
                writer.Append(new CanFrame(0, 0x124, [2], 100)));
        }
        finally { File.Delete(path); }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}.rbgframes");
}
