using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RbgCanInsight.Analysis;

namespace RbgCanInsight.App.Controls;

/// <summary>Two-series line plot (reference vs loaded trace) over a common relative time axis.</summary>
public sealed class SignalOverlayPlotControl : FrameworkElement
{
    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(
        nameof(Result), typeof(SignalOverlayResult), typeof(SignalOverlayPlotControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SignalOverlayResult? Result
    {
        get => (SignalOverlayResult?)GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 80 || height < 50) return;
        var foreground = (TryFindResource("ForegroundBrush") as Brush) ?? Brushes.Gray;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (Result is not { } result || (result.ReferenceChart.Count == 0 && result.SubjectChart.Count == 0))
        {
            DrawText(context, "No data", foreground, 12, new Point(10, 10));
            return;
        }

        var all = result.ReferenceChart.Concat(result.SubjectChart).ToArray();
        double minX = all.Min(p => p.RelativeSeconds), maxX = all.Max(p => p.RelativeSeconds);
        double minY = all.Min(p => p.Value), maxY = all.Max(p => p.Value);
        if (maxX <= minX) maxX = minX + 1e-6;
        if (maxY <= minY) { minY -= 1; maxY += 1; }
        const double left = 52, top = 10, right = 12, bottom = 26;
        var plot = new Rect(left, top, Math.Max(10, width - left - right), Math.Max(10, height - top - bottom));
        Point Map(OverlayChartPoint p) => new(
            plot.Left + (p.RelativeSeconds - minX) / (maxX - minX) * plot.Width,
            plot.Bottom - (p.Value - minY) / (maxY - minY) * plot.Height);

        var axisPen = new Pen(foreground, 0.5) { Brush = foreground };
        context.DrawRectangle(null, axisPen, plot);
        DrawText(context, maxY.ToString("G5", CultureInfo.InvariantCulture), foreground, 10, new Point(2, plot.Top - 2));
        DrawText(context, minY.ToString("G5", CultureInfo.InvariantCulture), foreground, 10, new Point(2, plot.Bottom - 12));
        DrawText(context, minX.ToString("F3", CultureInfo.InvariantCulture) + " s", foreground, 10, new Point(plot.Left, plot.Bottom + 4));
        DrawText(context, maxX.ToString("F3", CultureInfo.InvariantCulture) + " s", foreground, 10, new Point(plot.Right - 50, plot.Bottom + 4));

        Draw(context, result.ReferenceChart, Map, new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xD8)), 1.4));
        Draw(context, result.SubjectChart, Map, new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E)), 1.4));
        DrawText(context, "━ reference", new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xD8)), 11, new Point(plot.Left + 8, plot.Top + 4));
        DrawText(context, "━ loaded", new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E)), 11, new Point(plot.Left + 100, plot.Top + 4));
    }

    private static void Draw(DrawingContext context, IReadOnlyList<OverlayChartPoint> points,
        Func<OverlayChartPoint, Point> map, Pen pen)
    {
        if (points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(map(points[0]), false, false);
            for (var i = 1; i < points.Count; i++) stream.LineTo(map(points[i]), true, false);
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawText(DrawingContext context, string text, Brush brush, double size, Point at) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), at);
}
