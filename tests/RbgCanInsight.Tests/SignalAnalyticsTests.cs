using System;
using System.Collections.Generic;
using System.Linq;
using RbgCanInsight.Analysis.Analytics;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalAnalyticsTests
{
    private const long Ms = 1_000_000;

    private static AnalyticsSample[] Sine(double freq, double amp, double dc, double rate, int n) =>
        Enumerable.Range(0, n).Select(k => new AnalyticsSample((long)Math.Round(k * 1e9 / rate),
            dc + amp * Math.Sin(2 * Math.PI * freq * k / rate))).ToArray();

    private static AnalyticsSample[] S(params (long ms, double v)[] p) =>
        p.Select(x => new AnalyticsSample(x.ms * Ms, x.v)).ToArray();

    [Theory]
    [InlineData(FftWindow.Rectangular)]
    [InlineData(FftWindow.Hann)]
    [InlineData(FftWindow.Hamming)]
    [InlineData(FftWindow.Blackman)]
    public void Fft_SineBinCentred_FindsPeakAndAmplitude(FftWindow w)
    {
        var r = SignalAnalytics.Fft(Sine(125, 3.0, 10.0, 1000, 1024), 1000, ResampleMode.Linear, w, removeMean: true);
        Assert.Equal(1024, r.FftSize);
        Assert.Equal(125.0, r.PeakFrequencyHz, 6);
        Assert.Equal(3.0, r.PeakAmplitude, 4);
    }

    [Fact]
    public void Fft_PadsToPowerOfTwo()
    {
        var r = SignalAnalytics.Fft(Sine(50, 1, 0, 1000, 1000), 1000, ResampleMode.SampleAndHold, FftWindow.Hann);
        Assert.Equal(1000, r.GridPoints);
        Assert.Equal(1024, r.FftSize);
        Assert.Equal(513, r.Amplitudes.Length);
        Assert.InRange(r.PeakFrequencyHz, 48, 52);
        Assert.InRange(r.PeakAmplitude, 0.9, 1.05);
    }

    [Fact]
    public void Fft_ConstantSeries_DcOnly()
    {
        var c = Enumerable.Range(0, 64).Select(i => new AnalyticsSample(i * Ms, 5.0)).ToArray();
        var r = SignalAnalytics.Fft(c, 1000, ResampleMode.SampleAndHold, FftWindow.Rectangular, excludeDcFromPeak: false);
        Assert.Equal(0, r.PeakBin);
        Assert.Equal(5.0, r.Amplitudes[0], 9);
        Assert.All(r.Amplitudes.Skip(1), a => Assert.True(a < 1e-9));
    }

    [Fact]
    public void Fft_DcRemoval_ZeroesDcBin_AndExcludeDcPicksTone()
    {
        var s = Sine(125, 2.0, 50.0, 1000, 1024);
        var kept = SignalAnalytics.Fft(s, 1000, ResampleMode.Linear, FftWindow.Rectangular, removeMean: false, excludeDcFromPeak: false);
        Assert.Equal(0, kept.PeakBin);
        Assert.Equal(50.0, kept.PeakAmplitude, 6);
        var removed = SignalAnalytics.Fft(s, 1000, ResampleMode.Linear, FftWindow.Rectangular, removeMean: true, excludeDcFromPeak: false);
        Assert.True(removed.Amplitudes[0] < 1e-9);
        Assert.Equal(125.0, removed.PeakFrequencyHz, 6);
        var excl = SignalAnalytics.Fft(s, 1000, ResampleMode.Linear, FftWindow.Rectangular, removeMean: false, excludeDcFromPeak: true);
        Assert.Equal(125.0, excl.PeakFrequencyHz, 6);
    }

    [Fact]
    public void Fft_RejectsShortSeries_AndHugeGrids_AndBadRate()
    {
        Assert.Throws<ArgumentException>(() => SignalAnalytics.Fft(Sine(1, 1, 0, 100, 7), 100, ResampleMode.Linear, FftWindow.Hann));
        var huge = S((0, 0), (1, 1), (2, 0), (3, 1), (4, 0), (5, 1), (6, 0), (7000000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SignalAnalytics.Fft(huge, 1000, ResampleMode.Linear, FftWindow.Hann));
        Assert.Throws<ArgumentOutOfRangeException>(() => SignalAnalytics.Fft(Sine(1, 1, 0, 100, 100), 0, ResampleMode.Linear, FftWindow.Hann));
        // enough samples but grid at 1 Hz over ~1 s collapses below 8 points
        Assert.Throws<ArgumentException>(() => SignalAnalytics.Fft(Sine(1, 1, 0, 100, 100), 1, ResampleMode.Linear, FftWindow.Hann));
    }

    [Fact]
    public void Histogram_ValueOnEdges_AndNanCounted()
    {
        var series = S((0, 0), (1, 1), (2, 2), (3, double.NaN), (4, 3), (5, -1), (6, 4));
        var h = SignalAnalytics.Histogram(series, new[] { 0.0, 1.0, 2.0, 3.0 }, HistogramWeighting.BySample);
        Assert.Equal(new long[] { 1, 1, 2 }, h.Counts);   // 0 | 1 | 2 and 3 (last edge closed)
        Assert.Equal(1, h.UnderflowCount);                 // -1
        Assert.Equal(1, h.OverflowCount);                  // 4
        Assert.Equal(1, h.NanCount);
        Assert.Equal(6, h.Counts.Sum() + h.UnderflowCount + h.OverflowCount);
    }

    [Fact]
    public void Histogram_EqualWidthBins_AndTimeHeldWeights()
    {
        var series = S((0, 0), (1000, 10), (1500, 0));
        var h = SignalAnalytics.Histogram(series, 2, HistogramWeighting.ByTimeHeld);
        Assert.Equal(new[] { 0.0, 5.0, 10.0 }, h.Edges);
        Assert.Equal(new long[] { 2, 1 }, h.Counts);
        Assert.Equal(1.0, h.Weights[0], 9);   // first sample held 1 s; last sample 0
        Assert.Equal(0.5, h.Weights[1], 9);
    }

    [Fact]
    public void Histogram_ConstantSeries_WidensRange()
    {
        var h = SignalAnalytics.Histogram(S((0, 7), (1, 7)), 4, HistogramWeighting.BySample);
        Assert.Equal(2, h.Counts.Sum());
        Assert.Equal(0, h.UnderflowCount + h.OverflowCount);
    }

    [Fact]
    public void Summary_ConstantSeries()
    {
        var st = SignalAnalytics.Summarize(S((0, 4), (10, 4), (30, 4)));
        Assert.Equal(3, st.Count);
        Assert.Equal(4, st.Min);
        Assert.Equal(4, st.Max);
        Assert.Equal(0, st.StdDev);
        Assert.Equal(1, st.DistinctValues);
        Assert.Equal(0, st.Changes);
        Assert.Equal(30 * Ms, st.LongestConstantRunNs);
        Assert.Equal(4, st.Median);
    }

    [Fact]
    public void Summary_TimeWeightedMean_OnIrregularSampling()
    {
        // 0 held 9 s, 10 held 1 s -> time-weighted mean 1; sample mean 20/3
        var st = SignalAnalytics.Summarize(S((0, 0), (9000, 10), (10000, 10)));
        Assert.Equal(1.0, st.TimeWeightedMean, 9);
        Assert.Equal(20.0 / 3, st.Mean, 9);
        Assert.Equal(1, st.Changes);
        Assert.Equal(2, st.DistinctValues);
    }

    [Fact]
    public void Summary_PercentilesAndNan()
    {
        var series = Enumerable.Range(0, 101).Select(i => new AnalyticsSample(i * Ms, i))
            .Append(new AnalyticsSample(200 * Ms, double.NaN)).ToArray();
        var st = SignalAnalytics.Summarize(series);
        Assert.Equal(101, st.Count);
        Assert.Equal(1, st.NanCount);
        Assert.Equal(50, st.Median, 9);
        Assert.Equal(5, st.P5, 9);
        Assert.Equal(95, st.P95, 9);
        Assert.Equal(101, st.DistinctValues);
        Assert.Equal(100, st.Changes);
    }

    [Fact]
    public void Summary_Empty_IsNaN()
    {
        var st = SignalAnalytics.Summarize(Array.Empty<AnalyticsSample>());
        Assert.Equal(0, st.Count);
        Assert.True(double.IsNaN(st.Mean));
    }

    [Fact]
    public void XyPairs_HoldOnUnionTimestamps()
    {
        var x = S((0, 1), (20, 2));
        var y = S((10, 5), (20, 6), (30, 7));
        var p = SignalAnalytics.XyPairs(x, y);
        Assert.Equal(3, p.Count);   // starts at t=10 (first instant both valid)
        Assert.Equal(new XyPoint(10 * Ms, 1, 5), p[0]);
        Assert.Equal(new XyPoint(20 * Ms, 2, 6), p[1]);
        Assert.Equal(new XyPoint(30 * Ms, 2, 7), p[2]);
    }

    [Fact]
    public void Correlation_Perfect_AndAnti_AndRegression()
    {
        var xs = Enumerable.Range(0, 20).Select(i => new AnalyticsSample(i * Ms, i)).ToArray();
        var ys = xs.Select(s => s with { Value = 2 * s.Value + 1 }).ToArray();
        var neg = xs.Select(s => s with { Value = -3 * s.Value + 4 }).ToArray();
        var p = SignalAnalytics.XyPairs(xs, ys);
        Assert.Equal(1.0, SignalAnalytics.PearsonCorrelation(p), 9);
        var reg = SignalAnalytics.Regress(p);
        Assert.Equal(2.0, reg.Slope, 9);
        Assert.Equal(1.0, reg.Intercept, 9);
        Assert.Equal(1.0, reg.RSquared, 9);
        Assert.Equal(-1.0, SignalAnalytics.PearsonCorrelation(SignalAnalytics.XyPairs(xs, neg)), 9);
    }

    [Fact]
    public void Correlation_Degenerate_IsNaN()
    {
        var p = SignalAnalytics.XyPairs(S((0, 1), (1, 1), (2, 1)), S((0, 1), (1, 2), (2, 3)));
        Assert.True(double.IsNaN(SignalAnalytics.PearsonCorrelation(p)));
        Assert.True(double.IsNaN(SignalAnalytics.Regress(p).Slope));
    }

    [Fact]
    public void Envelope_RollingMinMax()
    {
        var env = SignalAnalytics.Envelope(S((0, 5), (100, 1), (200, 9), (300, 4), (400, 4)), 0.2);
        Assert.Equal((5.0, 5.0), (env[0].Min, env[0].Max));
        Assert.Equal((1.0, 9.0), (env[2].Min, env[2].Max));   // window [0,200]
        Assert.Equal((1.0, 9.0), (env[3].Min, env[3].Max));   // [100,300]
        Assert.Equal((4.0, 9.0), (env[4].Min, env[4].Max));   // [200,400]
    }

    [Fact]
    public void DetectStuck_ReturnsEvidenceIntervals()
    {
        var s = S((0, 1), (10, 2), (20, 2), (30, 2), (1000, 2), (1010, 3), (1020, 3));
        var stuck = SignalAnalytics.DetectStuck(s, 500 * Ms);
        var one = Assert.Single(stuck);
        Assert.Equal(1, one.FirstIndex);
        Assert.Equal(4, one.LastIndex);
        Assert.Equal(2.0, one.Value);
        Assert.Equal(990 * Ms, one.DurationNs);
    }

    [Fact]
    public void DetectSpikes_FlagsOutlierOnly()
    {
        var series = Enumerable.Range(0, 50).Select(i => new AnalyticsSample(i * Ms, 10 + (i % 2) * 0.1)).ToArray();
        series[25] = series[25] with { Value = 100 };
        var sp = Assert.Single(SignalAnalytics.DetectSpikes(series, 5));
        Assert.Equal(25, sp.Index);
        Assert.Empty(SignalAnalytics.DetectSpikes(S((0, 3), (1, 3), (2, 3), (3, 3)), 3));
    }

    [Fact]
    public void NonMonotonicTimestamps_Throw()
    {
        Assert.Throws<ArgumentException>(() => SignalAnalytics.Summarize(S((5, 1), (4, 1))));
    }
}
