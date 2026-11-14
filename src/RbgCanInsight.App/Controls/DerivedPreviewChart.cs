using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RbgCanInsight.Analysis.Derived;

namespace RbgCanInsight.App.Controls;

/// <summary>Minimal line chart for a derived series, drawn directly with DrawingContext. Undefined (NaN) samples
/// break the line. Columns are min/max decimated so very long series stay cheap.</summary>
public sealed class DerivedPreviewChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(SampleSeries), typeof(DerivedPreviewChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SampleSeries? Series
    {
        get => (SampleSeries?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 10 || h < 10) return;
        var back = Res("CanvasBackgroundBrush", Brushes.White);
        var text = Res("CanvasTextBrush", Brushes.Black);
        dc.DrawRectangle(back, null, new Rect(0, 0, w, h));
        var series = Series;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Label(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, text, dpi);

        if (series is null || series.Count == 0)
        {
            dc.DrawText(Label("Press Preview to compute the derived signal."), new Point(10, 10));
            return;
        }

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var v in series.Values)
        {
            if (!double.IsFinite(v)) continue;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        if (min > max)
        {
            dc.DrawText(Label("No finite values to draw (all samples are NaN)."), new Point(10, 10));
            return;
        }

        if (max - min < 1e-12) { min -= 1; max += 1; }
        const double left = 58, right = 10, top = 8, bottom = 22;
        var plot = new Rect(left, top, Math.Max(1, w - left - right), Math.Max(1, h - top - bottom));
        var gridPen = new Pen(Res("CanvasGridLineBrush", Brushes.LightGray), 1);
        dc.DrawRectangle(null, gridPen, plot);
        dc.DrawText(Label(max.ToString("G5", CultureInfo.InvariantCulture)), new Point(4, plot.Top - 2));
        dc.DrawText(Label(min.ToString("G5", CultureInfo.InvariantCulture)), new Point(4, plot.Bottom - 12));
        var t0 = series.Timestamps[0];
        var span = Math.Max(1, series.Timestamps[^1] - t0);
        dc.DrawText(Label("0 s"), new Point(plot.Left, plot.Bottom + 4));
        var endLabel = Label((span / 1e9).ToString("G5", CultureInfo.InvariantCulture) + " s");
        dc.DrawText(endLabel, new Point(plot.Right - endLabel.Width, plot.Bottom + 4));

        double X(long t) => plot.Left + (t - t0) / (double)span * plot.Width;
        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;
        var pen = new Pen(Res("AccentBrush", Brushes.SteelBlue), 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();

        // Per pixel column: first, min, max, last of the finite samples; NaN ends the current run.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var open = false;
            var i = 0;
            var n = series.Count;
            while (i < n)
            {
                var col = (int)Math.Floor((X(series.Timestamps[i]) - plot.Left));
                var j = i;
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity, first = double.NaN, last = double.NaN;
                var sawNan = false;
                while (j < n && (int)Math.Floor(X(series.Timestamps[j]) - plot.Left) == col)
                {
                    var v = series.Values[j];
                    if (double.IsFinite(v))
                    {
                        if (double.IsNaN(first)) first = v;
                        last = v; lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                    }
                    else sawNan = true;
                    j++;
                }

                if (!double.IsNaN(first))
                {
                    var x = plot.Left + col;
                    var xClamped = Math.Min(plot.Right, Math.Max(plot.Left, x));
                    var pts = new[] { first, lo, hi, last };
                    foreach (var v in pts)
                    {
                        var p = new Point(xClamped, Y(v));
                        if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                        else ctx.LineTo(p, true, false);
                    }
                    if (series.Count == 1) ctx.LineTo(new Point(xClamped + 3, Y(first)), true, false);
                }

                if (sawNan) open = false;
                i = j;
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
