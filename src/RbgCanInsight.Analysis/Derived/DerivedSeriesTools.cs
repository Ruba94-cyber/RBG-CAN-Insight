using System.Globalization;
using System.Text;
using RbgCanInsight.Dbc;

namespace RbgCanInsight.Analysis.Derived;

/// <summary>Summary of a derived series. Min/max/mean ignore NaN (undefined) samples.</summary>
public sealed record DerivedSeriesStats(int SampleCount, int NanCount, double Min, double Max, double Mean)
{
    public int FiniteCount => SampleCount - NanCount;

    public static DerivedSeriesStats Compute(SampleSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var nan = 0;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var sum = 0d;
        foreach (var v in series.Values)
        {
            if (!double.IsFinite(v)) { nan++; continue; }
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }

        var finite = series.Count - nan;
        return finite == 0
            ? new DerivedSeriesStats(series.Count, nan, double.NaN, double.NaN, double.NaN)
            : new DerivedSeriesStats(series.Count, nan, min, max, sum / finite);
    }

    public string ToText()
    {
        var c = CultureInfo.InvariantCulture;
        return FiniteCount == 0
            ? string.Create(c, $"Samples: {SampleCount} | NaN: {NanCount} | no finite values")
            : string.Create(c, $"Min: {Min:G6} | Max: {Max:G6} | Mean: {Mean:G6} | Samples: {SampleCount} | NaN: {NanCount}");
    }
}

public static class DerivedSeriesTools
{
    /// <summary>Convert a recorded timeline to the engine's input form (timeline samples are sorted).</summary>
    public static SampleSeries ToSampleSeries(SignalTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var s = timeline.Samples;
        var ts = new long[s.Count];
        var vs = new double[s.Count];
        for (var i = 0; i < s.Count; i++) { ts[i] = s[i].TimestampNanoseconds; vs[i] = s[i].Value; }
        return new SampleSeries(ts, vs);
    }

    /// <summary>Wraps a derived series as a (clearly non-recorded) timeline so existing plot windows can show it.
    /// Undefined (NaN) samples are dropped; frame indices are -1 (no source frame).</summary>
    public static SignalTimeline ToTimeline(string expression, SampleSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var text = (expression ?? string.Empty).Trim();
        var signal = new DbcSignal(
            "Derived: " + text, 0, 64, DbcByteOrder.Intel, false, 1, 0, 0, 0, string.Empty, string.Empty,
            Comment: "Derived signal computed offline from recorded samples. Not recorded on the bus.",
            ValueType: DbcSignalValueType.Float64);
        var message = new DbcMessage(0, "Derived (not recorded)", 0, string.Empty, false, [signal]);
        var timeline = new SignalTimeline(message, signal, "Derived (computed offline, not recorded)", -1, 0, false);
        for (var i = 0; i < series.Count; i++)
        {
            if (double.IsFinite(series.Values[i])) timeline.Add(series.Timestamps[i], series.Values[i], -1);
        }
        return timeline;
    }

    public static string ToCsv(string expression, SampleSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var sb = new StringBuilder();
        sb.Append("# Derived signal (computed offline from recorded samples, not recorded on the bus)\r\n");
        sb.Append("# Expression: ").Append((expression ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
        sb.Append("timestamp_ns,time_s,value\r\n");
        var c = CultureInfo.InvariantCulture;
        var t0 = series.Count > 0 ? series.Timestamps[0] : 0;
        for (var i = 0; i < series.Count; i++)
        {
            var v = series.Values[i];
            sb.Append(series.Timestamps[i].ToString(c)).Append(',')
              .Append(((series.Timestamps[i] - t0) / 1e9).ToString("R", c)).Append(',')
              .Append(double.IsFinite(v) ? v.ToString("R", c) : "NaN").Append("\r\n");
        }
        return sb.ToString();
    }
}
