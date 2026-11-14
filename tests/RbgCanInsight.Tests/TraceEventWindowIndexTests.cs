using RbgCanInsight.Analysis;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceEventWindowIndexTests
{
    private static CanFrame[] Frames(params long[] timestamps) =>
        timestamps.Select((t, i) =>
            new CanFrame(channel: i % 2, identifier: 0x123, payload: new byte[] { (byte)i },
                timestampNanoseconds: t, sequence: i + 1L)).ToArray();

    [Fact]
    public void ContextIncludesBothTimestampEndpointsAndAllEqualTimestampFrames()
    {
        var frames = Frames(0, 100, 100, 100, 200, 250);
        var context = TraceEventWindowIndex.Locate(frames, 100, 200);
        Assert.Equal(new[] { 1, 2, 3, 4 }, context.FrameIndices);
        Assert.Equal(4, context.TotalFramesInWindow);
        Assert.Equal(0, context.OmittedFrameIndices);
    }

    [Fact]
    public void ExcludesFramesOutsideRequestedPreAndPostWindow()
    {
        var frames = Frames(0, 10, 20, 30, 40);
        var context = TraceEventWindowIndex.Locate(frames, 11, 29);
        Assert.Equal(new[] { 2 }, context.FrameIndices);
        Assert.Equal(1, context.TotalFramesInWindow);
    }

    [Fact]
    public void BoundedContextReportsOmittedFrames()
    {
        var frames = Frames(0, 1, 2, 3, 4, 5);
        var context = TraceEventWindowIndex.Locate(frames, 0, 5, maxFrames: 2);
        Assert.Equal(new[] { 0, 1 }, context.FrameIndices);
        Assert.Equal(6, context.TotalFramesInWindow);
        Assert.Equal(4, context.OmittedFrameIndices);
        Assert.NotNull(context.Warning);
    }

    [Fact]
    public void ZeroLimitStillReportsAccurateCoverage()
    {
        var context = TraceEventWindowIndex.Locate(Frames(10, 20, 30),
            10, 30, maxFrames: 0);
        Assert.Empty(context.FrameIndices);
        Assert.Equal(3, context.OmittedFrameIndices);
    }

    [Fact]
    public void EmptySourceOrOutOfBoundsIntervalIsAnEmptyContext()
    {
        Assert.Empty(TraceEventWindowIndex.Locate([], 0, 100).FrameIndices);
        Assert.Empty(TraceEventWindowIndex.Locate(Frames(1, 2, 3), 4, 8).FrameIndices);
    }

    [Fact]
    public void InvalidRangeOrLimitIsRejectedBeforeIndexing()
    {
        Assert.Throws<ArgumentException>(() =>
            TraceEventWindowIndex.Locate(Frames(1, 2), 100, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TraceEventWindowIndex.Locate(Frames(1, 2), 0, 10, maxFrames: 2048));
        Assert.Throws<InvalidDataException>(() =>
            TraceEventWindowIndex.Locate(Frames(100, 20, 10), 0, 200));
    }

    [Fact]
    public void OriginalFrameIndicesRemainStableWhenChannelsInterleave()
    {
        var frames = Frames(0, 1, 2, 3, 4, 5);
        var context = TraceEventWindowIndex.Locate(frames, 1, 4);
        Assert.Equal(new[] { 1, 2, 3, 4 }, context.FrameIndices);
        Assert.Equal(1, frames[context.FrameIndices[0]].Channel);
        Assert.Equal(0, frames[context.FrameIndices[1]].Channel);
    }
}
