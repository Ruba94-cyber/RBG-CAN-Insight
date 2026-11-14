using System;
using System.Collections.Generic;

namespace RbgCanInsight.Analysis.Analytics;

/// <summary>One (timestamp, value) observation. Timestamps are nanoseconds and must be non-decreasing in a series.</summary>
public readonly record struct AnalyticsSample(long TimestampNs, double Value);

public enum HistogramWeighting
{
    /// <summary>Each sample contributes weight 1.</summary>
    BySample,
    /// <summary>Each sample contributes the seconds it is held until the next valid sample (last sample: 0).</summary>
    ByTimeHeld,
}

public enum ResampleMode { SampleAndHold, Linear }

public enum FftWindow { Rectangular, Hann, Hamming, Blackman }

/// <summary>
/// Histogram. Bin i is [Edges[i], Edges[i+1]) except the last bin, which is closed on the right.
/// Counts are exact sample counts; Weights are sample counts (BySample) or seconds (ByTimeHeld).
/// </summary>
public sealed record HistogramResult(
    double[] Edges, long[] Counts, double[] Weights,
    long UnderflowCount, double UnderflowWeight,
    long OverflowCount, double OverflowWeight,
    long NanCount);

public sealed record SummaryStatistics(
    long Count, long NanCount, double Min, double Max,
    double Mean, double TimeWeightedMean, double StdDev,
    double Median, double P5, double P95,
    int DistinctValues, long LongestConstantRunNs, long Changes);

public readonly record struct XyPoint(long TimestampNs, double X, double Y);

public sealed record LinearRegression(double Slope, double Intercept, double RSquared, int Count);

public sealed record FftResult(
    double[] FrequenciesHz, double[] Amplitudes,
    double SampleRateHz, int GridPoints, int FftSize,
    int PeakBin, double PeakFrequencyHz, double PeakAmplitude);

public readonly record struct EnvelopePoint(long TimestampNs, double Min, double Max);

/// <summary>A constant-value run. Span is first to last observed sample of the run (no hold extension).</summary>
public sealed record StuckInterval(int FirstIndex, int LastIndex, long StartNs, long EndNs, double Value)
{
    public long DurationNs => EndNs - StartNs;
}

public readonly record struct SpikePoint(int Index, long TimestampNs, double Value, double ZScore);

/// <summary>
/// Pure signal analytics over time-ordered (timestampNs, value) series. No UI dependencies.
/// NaN policy (all functions): NaN samples are ignored for computation and counted where a result
/// has a NanCount; they are removed before holding/interpolating, so the previous valid value is held
/// across them. Infinities are not special-cased and propagate as ordinary numbers.
/// Series must be non-decreasing in time (ArgumentException otherwise).
/// </summary>
public static class SignalAnalytics
{
    public const int MaxFftGridPoints = 1 << 22;
    public const int MinFftSamples = 8;

    // ---------------------------------------------------------------- helpers

    private static List<AnalyticsSample> Clean(IReadOnlyList<AnalyticsSample> series, out long nanCount)
    {
        ArgumentNullException.ThrowIfNull(series);
        var list = new List<AnalyticsSample>(series.Count);
        nanCount = 0;
        long prev = long.MinValue;
        foreach (var s in series)
        {
            if (s.TimestampNs < prev)
                throw new ArgumentException("Series timestamps must be non-decreasing.", nameof(series));
            prev = s.TimestampNs;
            if (double.IsNaN(s.Value)) { nanCount++; continue; }
            list.Add(s);
        }
        return list;
    }

    // -------------------------------------------------------------- histogram

    /// <summary>Equal-width histogram over [min,max] of the valid data. A constant series uses [v-0.5, v+0.5].</summary>
    public static HistogramResult Histogram(IReadOnlyList<AnalyticsSample> series, int binCount, HistogramWeighting weighting)
    {
        if (binCount < 1) throw new ArgumentOutOfRangeException(nameof(binCount));
        var data = Clean(series, out _);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var s in data) { if (s.Value < lo) lo = s.Value; if (s.Value > hi) hi = s.Value; }
        if (data.Count == 0) { lo = 0; hi = 1; }
        if (lo == hi) { lo -= 0.5; hi += 0.5; }
        var edges = new double[binCount + 1];
        for (int i = 0; i <= binCount; i++) edges[i] = i == binCount ? hi : lo + (hi - lo) * i / binCount;
        return Histogram(series, edges, weighting);
    }

    /// <summary>Histogram with explicit strictly increasing edges (at least 2).</summary>
    public static HistogramResult Histogram(IReadOnlyList<AnalyticsSample> series, double[] edges, HistogramWeighting weighting)
    {
        ArgumentNullException.ThrowIfNull(edges);
        if (edges.Length < 2) throw new ArgumentException("At least two edges are required.", nameof(edges));
        for (int i = 0; i < edges.Length; i++)
        {
            if (double.IsNaN(edges[i])) throw new ArgumentException("Edges must not be NaN.", nameof(edges));
            if (i > 0 && edges[i] <= edges[i - 1]) throw new ArgumentException("Edges must be strictly increasing.", nameof(edges));
        }
        var data = Clean(series, out long nan);
        int bins = edges.Length - 1;
        var counts = new long[bins];
        var weights = new double[bins];
        long uc = 0, oc = 0;
        double uw = 0, ow = 0;
        for (int i = 0; i < data.Count; i++)
        {
            double w = 1;
            if (weighting == HistogramWeighting.ByTimeHeld)
                w = i + 1 < data.Count ? (data[i + 1].TimestampNs - data[i].TimestampNs) / 1e9 : 0;
            double v = data[i].Value;
            if (v < edges[0]) { uc++; uw += w; }
            else if (v > edges[bins]) { oc++; ow += w; }
            else
            {
                int idx = Array.BinarySearch(edges, v);
                if (idx < 0) idx = ~idx - 1;       // bin whose left edge <= v
                if (idx >= bins) idx = bins - 1;    // v == last edge -> last (closed) bin
                counts[idx]++; weights[idx] += w;
            }
        }
        return new HistogramResult(edges, counts, weights, uc, uw, oc, ow, nan);
    }

    // ---------------------------------------------------------------- summary

    /// <summary>
    /// Summary statistics over valid samples. StdDev is the sample (n-1) deviation (0 for n=1).
    /// Percentiles use linear interpolation (type 7). TimeWeightedMean uses sample-and-hold
    /// (last sample has zero weight; falls back to Mean if total span is 0).
    /// LongestConstantRunNs is the first-to-last observed sample span of the longest equal-value run.
    /// Empty input yields Count 0 and NaN statistics.
    /// </summary>
    public static SummaryStatistics Summarize(IReadOnlyList<AnalyticsSample> series)
    {
        var d = Clean(series, out long nan);
        int n = d.Count;
        if (n == 0)
            return new SummaryStatistics(0, nan, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, 0, 0, 0);

        var vals = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++) { vals[i] = d[i].Value; sum += vals[i]; }
        double mean = sum / n;
        double ss = 0;
        foreach (var v in vals) ss += (v - mean) * (v - mean);
        double std = n > 1 ? Math.Sqrt(ss / (n - 1)) : 0;

        double wsum = 0, tsum = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            double dt = (d[i + 1].TimestampNs - d[i].TimestampNs) / 1e9;
            wsum += vals[i] * dt; tsum += dt;
        }
        double twMean = tsum > 0 ? wsum / tsum : mean;

        long changes = 0, longest = 0, runStart = d[0].TimestampNs;
        for (int i = 1; i < n; i++)
        {
            if (vals[i] != vals[i - 1])
            {
                changes++;
                longest = Math.Max(longest, d[i - 1].TimestampNs - runStart);
                runStart = d[i].TimestampNs;
            }
        }
        longest = Math.Max(longest, d[n - 1].TimestampNs - runStart);

        var sorted = (double[])vals.Clone();
        Array.Sort(sorted);
        int distinct = 1;
        for (int i = 1; i < n; i++) if (sorted[i] != sorted[i - 1]) distinct++;

        return new SummaryStatistics(n, nan, sorted[0], sorted[n - 1], mean, twMean, std,
            Percentile(sorted, 0.5), Percentile(sorted, 0.05), Percentile(sorted, 0.95),
            distinct, longest, changes);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        double pos = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (pos - lo) * (sorted[hi] - sorted[lo]);
    }

    // ----------------------------------------------------------------- XY

    /// <summary>
    /// Aligns two series with sample-and-hold on the union of their timestamps. Points start at the first
    /// instant where both have a valid value; each is held until its next valid sample. NaN samples are dropped first.
    /// </summary>
    public static List<XyPoint> XyPairs(IReadOnlyList<AnalyticsSample> seriesX, IReadOnlyList<AnalyticsSample> seriesY)
    {
        var xs = Clean(seriesX, out _);
        var ys = Clean(seriesY, out _);
        var result = new List<XyPoint>();
        int i = 0, j = 0;
        double cx = 0, cy = 0;
        bool hx = false, hy = false;
        while (i < xs.Count || j < ys.Count)
        {
            long t = i < xs.Count && (j >= ys.Count || xs[i].TimestampNs <= ys[j].TimestampNs)
                ? xs[i].TimestampNs : ys[j].TimestampNs;
            while (i < xs.Count && xs[i].TimestampNs == t) { cx = xs[i++].Value; hx = true; }
            while (j < ys.Count && ys[j].TimestampNs == t) { cy = ys[j++].Value; hy = true; }
            if (hx && hy) result.Add(new XyPoint(t, cx, cy));
        }
        return result;
    }

    /// <summary>Pearson correlation of aligned points (per union timestamp, not time-weighted). NaN if &lt;2 points or zero variance.</summary>
    public static double PearsonCorrelation(IReadOnlyList<XyPoint> pts)
    {
        int n = pts.Count;
        if (n < 2) return double.NaN;
        double mx = 0, my = 0;
        foreach (var p in pts) { mx += p.X; my += p.Y; }
        mx /= n; my /= n;
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts)
        {
            double dx = p.X - mx, dy = p.Y - my;
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }
        if (sxx == 0 || syy == 0) return double.NaN;
        return Math.Clamp(sxy / Math.Sqrt(sxx * syy), -1, 1);
    }

    /// <summary>Ordinary least squares y = slope*x + intercept. Slope/intercept/R² are NaN if &lt;2 points or x has zero variance; R² is NaN if y is constant.</summary>
    public static LinearRegression Regress(IReadOnlyList<XyPoint> pts)
    {
        int n = pts.Count;
        if (n < 2) return new LinearRegression(double.NaN, double.NaN, double.NaN, n);
        double mx = 0, my = 0;
        foreach (var p in pts) { mx += p.X; my += p.Y; }
        mx /= n; my /= n;
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts)
        {
            double dx = p.X - mx, dy = p.Y - my;
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }
        if (sxx == 0) return new LinearRegression(double.NaN, double.NaN, double.NaN, n);
        double slope = sxy / sxx;
        double intercept = my - slope * mx;
        double r2 = syy == 0 ? double.NaN : Math.Clamp(sxy * sxy / (sxx * syy), 0, 1);
        return new LinearRegression(slope, intercept, r2, n);
    }

    // -------------------------------------------------------------------- FFT

    /// <summary>
    /// One-sided amplitude spectrum. The series is resampled onto a uniform grid starting at the first valid
    /// timestamp at the caller-supplied <paramref name="gridRateHz"/> (never inferred), covering up to the last
    /// sample; grid point k is at t0 + k/rate. Optionally the mean is removed, then a periodic window is applied,
    /// then the data are zero-padded to the next power of two (frequency resolution = rate / FftSize; padding
    /// interpolates the spectrum but adds no information) and a radix-2 FFT is computed.
    /// Amplitudes are scaled by the window's coherent gain (2/sum(w); 1/sum(w) for DC and Nyquist), so a bin-centred
    /// sine of amplitude A reads ~A. Off-bin tones suffer scalloping loss; the peak frequency is the bin centre
    /// (no interpolation). Throws ArgumentException for &lt;8 valid samples or &lt;8 grid points and
    /// ArgumentOutOfRangeException for non-positive rate or more than 1&lt;&lt;22 grid points.
    /// </summary>
    public static FftResult Fft(IReadOnlyList<AnalyticsSample> series, double gridRateHz, ResampleMode resample,
        FftWindow window, bool removeMean = false, bool excludeDcFromPeak = true)
    {
        if (!(gridRateHz > 0) || double.IsInfinity(gridRateHz))
            throw new ArgumentOutOfRangeException(nameof(gridRateHz), "Grid rate must be a positive finite number.");
        var d = Clean(series, out _);
        if (d.Count < MinFftSamples)
            throw new ArgumentException($"FFT needs at least {MinFftSamples} valid samples; got {d.Count}.", nameof(series));

        double spanSec = (d[^1].TimestampNs - d[0].TimestampNs) / 1e9;
        double gridD = Math.Floor(spanSec * gridRateHz + 1e-9) + 1;
        if (gridD > MaxFftGridPoints)
            throw new ArgumentOutOfRangeException(nameof(gridRateHz),
                $"Grid rate {gridRateHz} Hz over {spanSec} s gives {gridD:F0} points; maximum is {MaxFftGridPoints}.");
        int m = (int)gridD;
        if (m < MinFftSamples)
            throw new ArgumentException($"Grid has only {m} points; need at least {MinFftSamples}. Raise the grid rate or use a longer series.", nameof(gridRateHz));

        var grid = new double[m];
        int idx = 0;
        long t0 = d[0].TimestampNs;
        for (int k = 0; k < m; k++)
        {
            long t = t0 + (long)Math.Round(k * 1e9 / gridRateHz);
            while (idx + 1 < d.Count && d[idx + 1].TimestampNs <= t) idx++;
            if (resample == ResampleMode.SampleAndHold || idx + 1 >= d.Count || t <= d[idx].TimestampNs)
                grid[k] = d[idx].Value;
            else
            {
                double f = (double)(t - d[idx].TimestampNs) / (d[idx + 1].TimestampNs - d[idx].TimestampNs);
                grid[k] = d[idx].Value + f * (d[idx + 1].Value - d[idx].Value);
            }
        }

        if (removeMean)
        {
            double mean = 0;
            foreach (var v in grid) mean += v;
            mean /= m;
            for (int k = 0; k < m; k++) grid[k] -= mean;
        }

        int size = 1;
        while (size < m) size <<= 1;
        var re = new double[size];
        var im = new double[size];
        double wsum = 0;
        for (int k = 0; k < m; k++)
        {
            double w = WindowValue(window, k, m);
            wsum += w;
            re[k] = grid[k] * w;
        }
        FftInPlace(re, im);

        int half = size / 2;
        var freqs = new double[half + 1];
        var amps = new double[half + 1];
        for (int k = 0; k <= half; k++)
        {
            freqs[k] = k * gridRateHz / size;
            double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / wsum;
            amps[k] = (k == 0 || k == half) ? mag : 2 * mag;
        }

        int peak = -1;
        for (int k = excludeDcFromPeak ? 1 : 0; k <= half; k++)
            if (peak < 0 || amps[k] > amps[peak]) peak = k;
        return new FftResult(freqs, amps, gridRateHz, m, size, peak, freqs[peak], amps[peak]);
    }

    private static double WindowValue(FftWindow w, int k, int n)
    {
        double x = 2 * Math.PI * k / n; // periodic window
        return w switch
        {
            FftWindow.Rectangular => 1,
            FftWindow.Hann => 0.5 - 0.5 * Math.Cos(x),
            FftWindow.Hamming => 0.54 - 0.46 * Math.Cos(x),
            FftWindow.Blackman => 0.42 - 0.5 * Math.Cos(x) + 0.08 * Math.Cos(2 * x),
            _ => throw new ArgumentOutOfRangeException(nameof(w)),
        };
    }

    private static void FftInPlace(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci;
                    double ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }

    // --------------------------------------------------------------- envelope

    /// <summary>Trailing rolling min/max: for each valid sample at t, the min/max over valid samples in [t - windowSeconds, t].</summary>
    public static List<EnvelopePoint> Envelope(IReadOnlyList<AnalyticsSample> series, double windowSeconds)
    {
        if (!(windowSeconds >= 0) || double.IsInfinity(windowSeconds))
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        var d = Clean(series, out _);
        long win = (long)Math.Round(windowSeconds * 1e9);
        var result = new List<EnvelopePoint>(d.Count);
        var minQ = new LinkedList<int>();
        var maxQ = new LinkedList<int>();
        for (int i = 0; i < d.Count; i++)
        {
            while (minQ.Last != null && d[minQ.Last.Value].Value >= d[i].Value) minQ.RemoveLast();
            minQ.AddLast(i);
            while (maxQ.Last != null && d[maxQ.Last.Value].Value <= d[i].Value) maxQ.RemoveLast();
            maxQ.AddLast(i);
            long cutoff = d[i].TimestampNs - win;
            while (d[minQ.First!.Value].TimestampNs < cutoff) minQ.RemoveFirst();
            while (d[maxQ.First!.Value].TimestampNs < cutoff) maxQ.RemoveFirst();
            result.Add(new EnvelopePoint(d[i].TimestampNs, d[minQ.First.Value].Value, d[maxQ.First.Value].Value));
        }
        return result;
    }

    // -------------------------------------------------------------- detectors

    /// <summary>
    /// Constant-value runs whose observed span (first to last sample of the run) is at least minDurationNs.
    /// Indices refer to the original series (NaN samples are skipped, so a run may span them). A run needs at
    /// least 2 samples. Conservative: time until the next differing sample is not counted.
    /// </summary>
    public static List<StuckInterval> DetectStuck(IReadOnlyList<AnalyticsSample> series, long minDurationNs)
    {
        if (minDurationNs < 0) throw new ArgumentOutOfRangeException(nameof(minDurationNs));
        ArgumentNullException.ThrowIfNull(series);
        var result = new List<StuckInterval>();
        int start = -1, last = -1;
        void Flush()
        {
            if (start >= 0 && last > start
                && series[last].TimestampNs - series[start].TimestampNs >= minDurationNs)
                result.Add(new StuckInterval(start, last, series[start].TimestampNs, series[last].TimestampNs, series[start].Value));
        }
        for (int i = 0; i < series.Count; i++)
        {
            if (double.IsNaN(series[i].Value)) continue;
            if (start >= 0 && series[i].Value == series[start].Value) { last = i; continue; }
            Flush();
            start = last = i;
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Outlier samples by robust modified z-score 0.6745*(x-median)/MAD; if MAD is 0 the mean absolute deviation
    /// is used instead (z = (x-median)/(1.2533*meanAD)). Returns samples with |z| &gt;= zThreshold, with original
    /// indices. Nothing is reported for a constant series. Global (not windowed) statistics: slow drifts
    /// or multi-level signals will flag the outer levels.
    /// </summary>
    public static List<SpikePoint> DetectSpikes(IReadOnlyList<AnalyticsSample> series, double zThreshold)
    {
        if (!(zThreshold > 0)) throw new ArgumentOutOfRangeException(nameof(zThreshold));
        ArgumentNullException.ThrowIfNull(series);
        var idxs = new List<int>();
        for (int i = 0; i < series.Count; i++) if (!double.IsNaN(series[i].Value)) idxs.Add(i);
        var result = new List<SpikePoint>();
        if (idxs.Count < 3) return result;
        var vals = new double[idxs.Count];
        for (int k = 0; k < vals.Length; k++) vals[k] = series[idxs[k]].Value;
        var sorted = (double[])vals.Clone();
        Array.Sort(sorted);
        double med = Percentile(sorted, 0.5);
        var dev = new double[vals.Length];
        double meanAd = 0;
        for (int k = 0; k < vals.Length; k++) { dev[k] = Math.Abs(vals[k] - med); meanAd += dev[k]; }
        meanAd /= vals.Length;
        Array.Sort(dev);
        double mad = Percentile(dev, 0.5);
        for (int k = 0; k < vals.Length; k++)
        {
            double z;
            if (mad > 0) z = 0.6745 * (vals[k] - med) / mad;
            else if (meanAd > 0) z = (vals[k] - med) / (1.2533 * meanAd);
            else continue;
            if (Math.Abs(z) >= zThreshold)
                result.Add(new SpikePoint(idxs[k], series[idxs[k]].TimestampNs, vals[k], z));
        }
        return result;
    }
}
