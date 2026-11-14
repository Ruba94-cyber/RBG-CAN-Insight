using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class AdaptiveCanFrameCollectorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "rbg-spill-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static CanFrame Frame(int i, long timestampMs) =>
        new(i % 3, (uint)(0x100 + i), [(byte)i, (byte)(i >> 8)], timestampMs * 1_000_000L,
            CanFrameFlags.None, sequence: i);

    private static IEnumerable<string> SpillFiles(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory) : [];

    [Fact]
    public void SmallCapturesStayInMemoryAndCreateNoFile()
    {
        using var collector = new AdaptiveCanFrameCollector(16, spillThresholdFrames: 100, _directory);
        for (var i = 0; i < 50; i++) collector.Add(Frame(i, i));
        using var result = collector.Complete(sortByTime: false);
        Assert.False(result.IsDiskBacked);
        Assert.Equal(50, result.Frames.Count);
        Assert.Empty(SpillFiles(_directory));
    }

    [Fact]
    public void LargeCapturesSpillWithoutLosingOrChangingAnyFrame()
    {
        var expected = Enumerable.Range(0, 1_000).Select(i => Frame(i, i)).ToArray();
        using var collector = new AdaptiveCanFrameCollector(16, spillThresholdFrames: 100, _directory);
        foreach (var frame in expected) collector.Add(frame);
        Assert.True(collector.HasSpilled);
        using var result = collector.Complete(sortByTime: false);

        Assert.True(result.IsDiskBacked);
        Assert.Equal(expected.Length, result.Frames.Count);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], result.Frames[i]);
        Assert.Single(SpillFiles(_directory));
    }

    [Fact]
    public void SpilledCaptureOutOfOrderIsSortedByTimestampThenSequence()
    {
        var shuffled = new List<CanFrame>();
        for (var i = 0; i < 500; i++) shuffled.Add(Frame(i, (i * 37) % 500)); // permutation of 0..499 ms
        shuffled.Add(Frame(500, 10)); // duplicate timestamp, later sequence
        using var collector = new AdaptiveCanFrameCollector(16, spillThresholdFrames: 50, _directory);
        foreach (var frame in shuffled) collector.Add(frame);
        using var result = collector.Complete(sortByTime: true);

        Assert.True(result.IsDiskBacked);
        Assert.Equal(shuffled.Count, result.Frames.Count);
        for (var i = 1; i < result.Frames.Count; i++)
        {
            var previous = result.Frames[i - 1];
            var current = result.Frames[i];
            Assert.True(
                previous.TimestampNanoseconds < current.TimestampNanoseconds ||
                (previous.TimestampNanoseconds == current.TimestampNanoseconds &&
                 previous.Sequence < current.Sequence),
                $"frame {i} is out of order");
        }

        // Same multiset of frames: nothing dropped or invented by the sort.
        Assert.Equal(
            shuffled.Select(f => f.Sequence).OrderBy(s => s),
            result.Frames.Select(f => f.Sequence).OrderBy(s => s));
        Assert.Single(SpillFiles(_directory)); // the unsorted intermediate store is gone
    }

    [Fact]
    public void DisposingTheResultDeletesTheSpillFile()
    {
        using var collector = new AdaptiveCanFrameCollector(16, spillThresholdFrames: 10, _directory);
        for (var i = 0; i < 40; i++) collector.Add(Frame(i, i));
        var result = collector.Complete(sortByTime: false);
        Assert.Single(SpillFiles(_directory));
        result.Dispose();
        Assert.Empty(SpillFiles(_directory));
    }

    [Fact]
    public void AbandonedImportRemovesItsPartialFile()
    {
        var collector = new AdaptiveCanFrameCollector(16, spillThresholdFrames: 10, _directory);
        for (var i = 0; i < 40; i++) collector.Add(Frame(i, i));
        Assert.Single(SpillFiles(_directory));
        collector.Dispose();
        Assert.Empty(SpillFiles(_directory));
    }

    [Fact]
    public void StaleSpillFilesAreCleanedButFreshOnesAreKept()
    {
        Directory.CreateDirectory(_directory);
        var stale = Path.Combine(_directory, "rbg-frames-stale.bin");
        var fresh = Path.Combine(_directory, "rbg-frames-fresh.bin");
        var unrelated = Path.Combine(_directory, "notes.txt");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.WriteAllText(unrelated, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        AdaptiveCanFrameCollector.CleanUpStaleSpillFiles(_directory, TimeSpan.FromHours(1));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
    }
}
