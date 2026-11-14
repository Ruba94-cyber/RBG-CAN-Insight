using RbgCanInsight.Analysis.Analytics;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class ChartMathTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(0.13, 0.97)]
    [InlineData(-3.2, 47)]
    [InlineData(1000, 1000.5)]
    [InlineData(-1e6, 2e6)]
    [InlineData(5, 5)]
    public void TicksCoverTheRangeOnAOneTwoFiveGrid(double min, double max)
    {
        var ticks = ChartMath.NiceTicks(min, max);
        Assert.True(ticks.Length >= 2);
        Assert.True(ticks[0] <= min);
        Assert.True(ticks[^1] >= max);
        double step = ticks[1] - ticks[0];
        double mant = step / System.Math.Pow(10, System.Math.Floor(System.Math.Log10(step)));
        Assert.Contains(System.Math.Round(mant, 6), new[] { 1d, 2d, 5d, 10d });
        for (int i = 2; i < ticks.Length; i++)
            Assert.Equal(step, ticks[i] - ticks[i - 1], 6);
    }

    [Fact]
    public void TickCountStaysReasonable()
    {
        Assert.InRange(ChartMath.NiceTicks(0, 100, 6).Length, 4, 12);
    }

    [Theory]
    [InlineData(0.7, 1)]
    [InlineData(1.5, 2)]
    [InlineData(3, 5)]
    [InlineData(7, 10)]
    [InlineData(0.034, 0.05)]
    public void NiceStepRoundsUp(double raw, double expected) =>
        Assert.Equal(expected, ChartMath.NiceStep(raw), 9);

    [Fact]
    public void MedianRateIsDerivedFromIntervals()
    {
        var s = Enumerable.Range(0, 20).Select(i => new AnalyticsSample(i * 10_000_000L, i)).ToList();
        Assert.Equal(100d, ChartMath.MedianSampleRateHz(s)!.Value, 6);
        Assert.Null(ChartMath.MedianSampleRateHz([new AnalyticsSample(1, 1)]));
    }

    [Fact]
    public void TopPeaksFindsTheInjectedTones()
    {
        const double rate = 100;
        var s = Enumerable.Range(0, 1024).Select(i =>
            new AnalyticsSample((long)(i * 1e9 / rate),
                2 * System.Math.Sin(2 * System.Math.PI * 10 * i / rate) + System.Math.Sin(2 * System.Math.PI * 25 * i / rate))).ToList();
        var fft = SignalAnalytics.Fft(s, rate, ResampleMode.Linear, FftWindow.Hann, removeMean: true);
        var peaks = ChartMath.TopPeaks(fft, 5);
        Assert.InRange(peaks[0].FrequencyHz, 9.5, 10.5);
        Assert.InRange(peaks[1].FrequencyHz, 24.5, 25.5);
        Assert.True(peaks.Count <= 5);
    }
}
