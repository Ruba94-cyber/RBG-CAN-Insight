using RbgCanInsight.Analysis;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceFrameSelectionIndexTests
{
    private static CanFrame Frame(uint id, int channel = 0, bool extended = false,
        int timeMs = 0, byte payload = 0) =>
        new(channel, id, [payload], timeMs * 1_000_000L,
            extended ? CanFrameFlags.Extended : CanFrameFlags.None);

    [Fact]
    public void FilterResultsStaySortedAndReplayPrefixNeverIncludesFutureFrames()
    {
        var frames = Enumerable.Range(0, 20)
            .Select(i => Frame((uint)(0x100 + i % 3), timeMs: i, payload: (byte)i))
            .ToArray();
        var index = new TraceFrameSelectionIndex(frames);
        var matching = index.BuildMatchingIndex(f => f.Identifier == 0x101);
        Assert.Equal(new[] { 1, 4, 7, 10, 13, 16, 19 }, matching);

        Assert.Equal(0, TraceFrameSelectionIndex.VisiblePrefixLength(matching, -1));
        Assert.Equal(0, TraceFrameSelectionIndex.VisiblePrefixLength(matching, 0));
        Assert.Equal(1, TraceFrameSelectionIndex.VisiblePrefixLength(matching, 1));
        Assert.Equal(2, TraceFrameSelectionIndex.VisiblePrefixLength(matching, 6));
        Assert.Equal(4, TraceFrameSelectionIndex.VisiblePrefixLength(matching, 10));
        Assert.Equal(7, TraceFrameSelectionIndex.VisiblePrefixLength(matching, 20));
    }

    [Fact]
    public void CancelledFilterThrowsInsteadOfReturningAPartialResult()
    {
        var frames = Enumerable.Range(0, 100_000).Select(i => Frame(0x100, timeMs: i)).ToArray();
        var index = new TraceFrameSelectionIndex(frames);
        using var cancellation = new CancellationTokenSource();
        var seen = 0;
        Assert.Throws<OperationCanceledException>(() => index.BuildMatchingIndex(
            _ => { if (++seen == 40_000) cancellation.Cancel(); return true; },
            null, cancellation.Token));
        Assert.True(seen < frames.Length, "the scan must stop soon after cancellation");
    }

    [Fact]
    public void FilterReportsMonotonicProgressAndSameResultAsUnobservedScan()
    {
        var frames = Enumerable.Range(0, 100_000).Select(i =>
            Frame((uint)(0x100 + i % 4), timeMs: i)).ToArray();
        var index = new TraceFrameSelectionIndex(frames);
        var reported = new List<double>();
        var observed = index.BuildMatchingIndex(f => f.Identifier == 0x102, null, default, reported.Add);
        var plain = index.BuildMatchingIndex(f => f.Identifier == 0x102);
        Assert.Equal(plain, observed);
        Assert.NotEmpty(reported);
        Assert.Equal(reported.OrderBy(v => v), reported);
        Assert.All(reported, v => Assert.InRange(v, 0d, 1d));
    }

    [Fact]
    public void ComposedEligibilityAndPredicatePreserveSourceIndices()
    {
        var frames = Enumerable.Range(0, 20).Select(i =>
            Frame((uint)(0x100 + i % 2), payload: (byte)i)).ToArray();
        var index = new TraceFrameSelectionIndex(frames);
        var matches = index.BuildMatchingIndex(
            f => f.Identifier == 0x100,
            i => i >= 4 && i <= 16 && i % 4 == 0);
        Assert.Equal(new[] { 4, 8, 12, 16 }, matches);
        Assert.Empty(index.BuildMatchingIndex(_ => false));
        Assert.Equal(0, TraceFrameSelectionIndex.VisiblePrefixLength([], 100));
    }

    [Fact]
    public void LatestModeSeparatesBusAndElevenVersusTwentyNineBitNumericId()
    {
        var frames = new[]
        {
            Frame(0x100, channel: 0, timeMs: 1),
            Frame(0x100, channel: 0, extended: true, timeMs: 2),
            Frame(0x100, channel: 1, timeMs: 3),
            Frame(0x100, channel: 0, timeMs: 4),
            Frame(0x100, channel: 0, extended: true, timeMs: 5),
            Frame(0x100, channel: 1, timeMs: 6),
        };
        var index = new TraceFrameSelectionIndex(frames);
        Assert.Equal(new[] { 0, 1, 2 }, index.LatestIndicesThrough(2));
        Assert.Equal(new[] { 2, 3, 4 }, index.LatestIndicesThrough(4));
        Assert.Equal(new[] { 3, 4, 5 }, index.LatestIndicesThrough(5));
        Assert.Equal(6, index.LatestScannedFrames);
    }

    [Fact]
    public void ForwardPlaybackDoesNotRescanEarlierFramesAndRewindRemovesFutureFrames()
    {
        var frames = Enumerable.Range(0, 100_000).Select(i =>
            Frame((uint)(0x100 + i % 40), channel: i % 2, timeMs: i)).ToArray();
        var index = new TraceFrameSelectionIndex(frames);
        var first = index.LatestIndicesThrough(49_999);
        Assert.Equal(50_000, index.LatestScannedFrames);
        Assert.Equal(40, first.Length);
        var again = index.LatestIndicesThrough(49_999);
        Assert.Equal(50_000, index.LatestScannedFrames); // Same cursor: zero frame scans
        Assert.Equal(first, again);
        index.LatestIndicesThrough(60_000);
        Assert.Equal(60_001, index.LatestScannedFrames); // Only 10,001 new frames
        var rewind = index.LatestIndicesThrough(19);
        Assert.Equal(20, rewind.Length); // no source index beyond rewind
        Assert.All(rewind, frameIndex => Assert.InRange(frameIndex, 0, 19));
        Assert.Equal(20, index.LatestScannedFrames - 60_001); // rewind reconstructs only 20
    }

    [Fact]
    public void EmptyAndNegativeReplayPositionsAreSafe()
    {
        var empty = new TraceFrameSelectionIndex([]);
        Assert.Empty(empty.LatestIndicesThrough(-1));
        Assert.Empty(empty.LatestIndicesThrough(1000));
        Assert.Empty(empty.BuildMatchingIndex(null));
        var index = new TraceFrameSelectionIndex([Frame(0x100)]);
        Assert.Empty(index.LatestIndicesThrough(-1));
        Assert.Equal(new[] { 0 }, index.LatestIndicesThrough(100));
    }

    [Fact]
    public void MillionFrameFilterIndexesUseOnlyFourBytesPerMatchedFrameAndLogTimePrefix()
    {
        // Synthetic source-index fixture: no WPF or real BLF dependency.
        // Do not enforce wall-clock performance on a shared CI machine.
        const int length = 1_000_000;
        var frames = Enumerable.Range(0, length).Select(i =>
            Frame((uint)(0x100 + i % 8), payload: (byte)i)).ToArray();
        var index = new TraceFrameSelectionIndex(frames);
        var matches = index.BuildMatchingIndex(f => f.Identifier == 0x102);
        Assert.Equal(length / 8, matches.Length);
        Assert.Equal(12_500, TraceFrameSelectionIndex.VisiblePrefixLength(matches, 99_999));
        Assert.Equal(matches.Length,
            TraceFrameSelectionIndex.VisiblePrefixLength(matches, length - 1));
        Assert.Equal(12_500, TraceFrameSelectionIndex.VisiblePrefixLength(matches, 99_999));
        // Retained result is one int[]; it contains source indices, no frames or payloads.
        Assert.Equal(sizeof(int) * (length / 8), sizeof(int) * matches.Length);
    }

    [Fact]
    public void FocusedSourceLatestModeNeverUsesAnotherRecordingsSameId()
    {
        // Two independent loggers recorded the same CAN identifier on the
        // same channel. The latest row for source B must never be overwritten
        // by the later row of source A in a focused notebook-like Trace view.
        var frames = new[]
        {
            Frame(0x123, timeMs: 0, payload: 0xA0), // A
            Frame(0x123, timeMs: 1, payload: 0xB0), // B
            Frame(0x123, timeMs: 2, payload: 0xA1), // A
            Frame(0x200, timeMs: 3, payload: 0xB1), // B
            Frame(0x123, timeMs: 4, payload: 0xB2), // B
            Frame(0x123, timeMs: 5, payload: 0xA2), // A
        };
        var index = new TraceFrameSelectionIndex(frames);
        Func<int, bool> sourceA = i => i is 0 or 2 or 5;
        Func<int, bool> sourceB = i => i is 1 or 3 or 4;

        Assert.Equal(new[] { 1 }, index.LatestIndicesThrough(2, sourceB, scopeToken: 1));
        Assert.Equal(new[] { 3, 4 }, index.LatestIndicesThrough(5, sourceB, scopeToken: 1));
        var scanned = index.LatestScannedFrames;
        Assert.Equal(new[] { 3, 4 }, index.LatestIndicesThrough(5, sourceB, scopeToken: 1));
        Assert.Equal(scanned, index.LatestScannedFrames); // no replay rescan

        Assert.Equal(new[] { 5 }, index.LatestIndicesThrough(5, sourceA, scopeToken: 0));
        Assert.Equal(new[] { 3, 4 }, index.LatestIndicesThrough(5, sourceB, scopeToken: 1));
        // Backward seek rebuilds: B's 0x123 latest through frame 3 is frame 1, 0x200 is 3.
        Assert.Equal(new[] { 1, 3 }, index.LatestIndicesThrough(3, sourceB, scopeToken: 1));

        // Clear focus: restore the global latest-by-ID view, with no cross-scope cache.
        Assert.Equal(new[] { 3, 5 }, index.LatestIndicesThrough(5));
        Assert.Equal(new[] { 1, 3, 4 }, index.BuildMatchingIndex(null, sourceB));
        Assert.Equal(new[] { 1, 4 }, index.BuildMatchingIndex(
            f => f.Identifier == 0x123, sourceB));
    }

    [Fact]
    public void FocusedSourcesCanBeEmptyWithoutLeakingRowsFromPreviousScope()
    {
        var index = new TraceFrameSelectionIndex([Frame(0x123), Frame(0x123)]);
        Assert.Equal(new[] { 1 }, index.LatestIndicesThrough(1));
        Assert.Empty(index.LatestIndicesThrough(1, _ => false, scopeToken: 9));
        Assert.Empty(index.BuildMatchingIndex(null, _ => false));
        Assert.Equal(new[] { 1 }, index.LatestIndicesThrough(1));
    }

    [Fact]
    public void PhysicalSourceHistoryPreventsFalsePayloadChangesFromOtherLoggers()
    {
        var frames = new[]
        {
            Frame(0x123, payload: 0x01), // source A
            Frame(0x123, payload: 0xFF), // source B
            Frame(0x123, payload: 0x02), // source A
            Frame(0x123, payload: 0xFF), // source B (unchanged)
        };
        var (global, bySource) = TraceFrameSelectionIndex.BuildPreviousIndices(
            frames, i => i % 2);
        Assert.Equal(new[] { -1, 0, 1, 2 }, global);
        Assert.Equal(new[] { -1, -1, 0, 1 }, bySource);

        // Source B's second row must compare with its own first row, not with A.
        Assert.Equal(frames[bySource[3]].GetPayload(), frames[3].GetPayload());
        Assert.NotEqual(frames[global[3]].GetPayload(), frames[3].GetPayload());

        var (singleGlobal, singleLocal) = TraceFrameSelectionIndex.BuildPreviousIndices(frames);
        Assert.Same(singleGlobal, singleLocal); // no extra index in single-file sessions
    }

    [Fact]
    public void PhysicalSourceHistoryAlsoSeparatesExtendedAndPhysicalCanChannels()
    {
        var frames = new[]
        {
            Frame(0x123, channel: 0),
            Frame(0x123, channel: 1),
            Frame(0x123, channel: 0, extended: true),
            Frame(0x123, channel: 0),
            Frame(0x123, channel: 0, extended: true),
        };
        var (global, bySource) = TraceFrameSelectionIndex.BuildPreviousIndices(
            frames, _ => 0);
        Assert.Equal(new[] { -1, -1, -1, 0, 2 }, global);
        Assert.Equal(global, bySource);
    }
}
