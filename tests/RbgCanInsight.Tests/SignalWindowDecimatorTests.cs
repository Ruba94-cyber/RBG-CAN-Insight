using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalWindowDecimatorTests
{
    [Fact]
    public void KeepsEndpointsShortSpikeAndChronologicalOrderInsideOnePixel()
    {
        var source = new SignalSample[]
        {
            new(1, 2, 11),
            new(2, 9, 12),
            new(3, -4, 13),
            new(4, 3, 14),
            new(5, 6, 15)
        };
        var display = SignalWindowDecimator.Decimate(source, 1, 5, 1);
        Assert.Equal(new[] { 11, 12, 13, 15 }, display.Select(point => point.FrameIndex));
        Assert.Equal(new[] { 2d, 9d, -4d, 6d }, display.Select(point => point.Value));
    }

    [Fact]
    public void NoSamplesInWindowDoesNotInventHeldObservedFrames()
    {
        var source = new SignalSample[] { new(100, 7, 1), new(500, 9, 2) };
        Assert.Empty(SignalWindowDecimator.Decimate(source, 200, 300, 200));
        var visible = SignalWindowDecimator.Decimate(source, 200, 500, 200);
        Assert.Equal(new[] { 1, 2 }, visible.Select(point => point.FrameIndex));
    }

    [Fact]
    public void LongRecordingPixelMathCannotOverflowAndKeepsLastPoint()
    {
        var t0 = long.MinValue + 100;
        var end = long.MaxValue - 100;
        var source = new SignalSample[]
        {
            new(t0, 1, 1),
            new(0, 20, 2),
            new(end, 4, 3)
        };
        var result = SignalWindowDecimator.Decimate(source, t0, end, 8);
        Assert.Equal(new[] { 1, 2, 3 }, result.Select(point => point.FrameIndex));
    }

    [Fact]
    public void DenseConstantSignalStaysBoundedByCanvasPixels()
    {
        var samples = Enumerable.Range(0, 100_000)
            .Select(i => new SignalSample(i * 10L, 4, i)).ToArray();
        var displayed = SignalWindowDecimator.Decimate(samples, 0, 999_990, 120);
        Assert.True(displayed.Length <= 481);
        Assert.Equal(0, displayed[0].FrameIndex);
        Assert.Equal(99_999, displayed[^1].FrameIndex);
    }

    [Fact]
    public void EqualTimestampIndependentCANFramesRemainInSourceOrder()
    {
        var source = new SignalSample[]
        {
            new(100, 0, 0), new(100, 1, 1), new(100, 2, 2)
        };
        var result = SignalWindowDecimator.Decimate(source, 100, 100, 1);
        Assert.Equal(new[] { 0, 2 }, result.Select(point => point.FrameIndex));
    }
}
