using System;
using System.Collections.Generic;

namespace RbgCanInsight.Analysis.Analytics;

/// <summary>A spectrum peak: a bin that is strictly higher than its neighbours.</summary>
public readonly record struct SpectrumPeak(int Bin, double FrequencyHz, double Amplitude);

/// <summary>UI-independent helpers for the chart window: axis ticks, default rates, peak picking.</summary>
public static class ChartMath
{
    /// <summary>
    /// Ticks on a 1-2-5 x 10^n grid covering [min, max]: first tick &lt;= min and last tick &gt;= max.
    /// Aims for about <paramref name="targetCount"/> ticks. A degenerate range (min == max) is widened.
    /// </summary>
    public static double[] NiceTicks(double min, double max, int targetCount = 6)
    {
        if (double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max))
            return [0, 1];
        if (min > max) (min, max) = (max, min);
        if (targetCount < 2) targetCount = 2;
        if (min == max)
        {
            double pad = min == 0 ? 0.5 : Math.Abs(min) * 0.05;
            min -= pad; max += pad;
        }
        double step = NiceStep((max - min) / (targetCount - 1));
        double first = Math.Floor(min / step + 1e-9) * step;
        var ticks = new List<double>();
        for (int i = 0; i < 1000; i++)
        {
            double t = Math.Round(first + i * step, 12);
            ticks.Add(t);
            if (t >= max - step * 1e-9) break;
        }
        return ticks.ToArray();
    }

    /// <summary>Rounds a raw step up to the nearest 1, 2 or 5 times a power of ten.</summary>
    public static double NiceStep(double rawStep)
    {
        if (!(rawStep > 0) || double.IsInfinity(rawStep)) return 1;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        double f = rawStep / pow;
        double nice = f <= 1.0000001 ? 1 : f <= 2.0000001 ? 2 : f <= 5.0000001 ? 5 : 10;
        return nice * pow;
    }

    /// <summary>Sample rate in Hz from the median positive interval, or null when it cannot be derived.</summary>
    public static double? MedianSampleRateHz(IReadOnlyList<AnalyticsSample> series)
    {
        var intervals = new List<long>();
        for (int i = 1; i < series.Count; i++)
            if (series[i].TimestampNs > series[i - 1].TimestampNs)
                intervals.Add(series[i].TimestampNs - series[i - 1].TimestampNs);
        if (intervals.Count == 0) return null;
        intervals.Sort();
        return 1e9 / intervals[intervals.Count / 2];
    }

    /// <summary>Top local maxima (strictly above both neighbours, DC excluded) by descending amplitude.</summary>
    public static List<SpectrumPeak> TopPeaks(FftResult fft, int count)
    {
        var peaks = new List<SpectrumPeak>();
        var a = fft.Amplitudes;
        for (int k = 1; k < a.Length; k++)
        {
            double left = a[k - 1];
            double right = k + 1 < a.Length ? a[k + 1] : double.NegativeInfinity;
            if (a[k] > left && a[k] >= right && a[k] > 0)
                peaks.Add(new SpectrumPeak(k, fft.FrequenciesHz[k], a[k]));
        }
        peaks.Sort((x, y) => y.Amplitude.CompareTo(x.Amplitude));
        if (peaks.Count > count) peaks.RemoveRange(count, peaks.Count - count);
        return peaks;
    }
}
