using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.Controls;

/// <summary>
/// Renders one decoded signal from two different offline traces overlaid on a shared,
/// trace-relative time axis (seconds since each trace's own first frame, since the two files
/// were not necessarily recorded starting at the same wall-clock moment) — turning a "base last
/// vs compare last" text row into an actual shape comparison.
/// </summary>
public sealed class CompareDiffChartControl : FrameworkElement
{
    public static readonly DependencyProperty BaseReportProperty =
        DependencyProperty.Register(
            nameof(BaseReport),
            typeof(SignalChangeReport),
            typeof(CompareDiffChartControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompareReportProperty =
        DependencyProperty.Register(
            nameof(CompareReport),
            typeof(SignalChangeReport),
            typeof(CompareDiffChartControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private Brush _background = Frozen(0xF2, 0xF4, 0xF6);
    private Brush _mutedText = Frozen(0x1F, 0x27, 0x33);
    private Pen _gridPen = FrozenPen(Frozen(0xD8, 0xDE, 0xE6), 1);
    private Pen _basePen = FrozenPen(Frozen(0x2A, 0x78, 0xD6), 1.75);
    private Pen _comparePen = DashedPen(Frozen(0xC9, 0x43, 0x3F));

    public CompareDiffChartControl()
    {
        Loaded += (_, _) =>
        {
            ResolveThemeBrushes();
            ThemeManager.ThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ResolveThemeBrushes();

    private void ResolveThemeBrushes()
    {
        _background = TryFindResource("CanvasBackgroundBrush") as Brush ?? _background;
        _mutedText = TryFindResource("CanvasTextBrush") as Brush ?? _mutedText;
        _gridPen = FrozenPen(TryFindResource("CanvasGridLineBrush") as Brush ?? _gridPen.Brush, 1);
        _basePen = FrozenPen(TryFindResource("PlotSeries1Brush") as Brush ?? _basePen.Brush, 1.75);
        _comparePen = DashedPen(TryFindResource("PlotSeries8Brush") as Brush ?? _comparePen.Brush);
        InvalidateVisual();
    }

    public SignalChangeReport? BaseReport
    {
        get => (SignalChangeReport?)GetValue(BaseReportProperty);
        set => SetValue(BaseReportProperty, value);
    }

    public SignalChangeReport? CompareReport
    {
        get => (SignalChangeReport?)GetValue(CompareReportProperty);
        set => SetValue(CompareReportProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(_background, null, new Rect(RenderSize));
        if (ActualWidth < 20 || ActualHeight < 20)
        {
            return;
        }

        var baseSamples = BaseReport?.Timeline.Samples;
        var compareSamples = CompareReport?.Timeline.Samples;
        var hasBase = baseSamples is { Count: > 0 };
        var hasCompare = compareSamples is { Count: > 0 };
        if (!hasBase && !hasCompare)
        {
            DrawCentered(dc, "No samples in either trace for this signal.");
            return;
        }

        var baseStart = hasBase ? baseSamples![0].TimestampNanoseconds : 0;
        var compareStart = hasCompare ? compareSamples![0].TimestampNanoseconds : 0;
        var maxDurationNs = Math.Max(
            hasBase ? baseSamples![^1].TimestampNanoseconds - baseStart : 0,
            hasCompare ? compareSamples![^1].TimestampNanoseconds - compareStart : 0);
        maxDurationNs = Math.Max(1, maxDurationNs);

        var minValue = double.MaxValue;
        var maxValue = double.MinValue;
        if (hasBase)
        {
            foreach (var sample in baseSamples!)
            {
                minValue = Math.Min(minValue, sample.Value);
                maxValue = Math.Max(maxValue, sample.Value);
            }
        }

        if (hasCompare)
        {
            foreach (var sample in compareSamples!)
            {
                minValue = Math.Min(minValue, sample.Value);
                maxValue = Math.Max(maxValue, sample.Value);
            }
        }

        if (maxValue <= minValue)
        {
            maxValue = minValue + 1;
        }

        const double leftAxis = 46;
        const double bottomAxis = 22;
        var chartWidth = Math.Max(10, ActualWidth - leftAxis - 10);
        var chartHeight = Math.Max(10, ActualHeight - bottomAxis - 6);

        for (var i = 0; i <= 4; i++)
        {
            var y = 6 + chartHeight * i / 4;
            dc.DrawLine(_gridPen, new Point(leftAxis, y), new Point(leftAxis + chartWidth, y));
            var value = maxValue - (maxValue - minValue) * i / 4;
            DrawText(dc, value.ToString("G4", CultureInfo.InvariantCulture), new Point(2, y - 6));
        }

        if (hasBase)
        {
            DrawSeries(dc, baseSamples!, baseStart, maxDurationNs, minValue, maxValue, leftAxis, chartWidth, chartHeight, _basePen);
        }

        if (hasCompare)
        {
            DrawSeries(dc, compareSamples!, compareStart, maxDurationNs, minValue, maxValue, leftAxis, chartWidth, chartHeight, _comparePen);
        }

        DrawText(dc, "0 s", new Point(leftAxis, ActualHeight - bottomAxis + 4));
        var endLabel = (maxDurationNs / 1_000_000_000d).ToString("F2", CultureInfo.InvariantCulture) + " s";
        DrawText(dc, endLabel, new Point(leftAxis + chartWidth - 30, ActualHeight - bottomAxis + 4));

        var unit = BaseReport?.Unit ?? CompareReport?.Unit ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(unit))
        {
            DrawText(dc, unit, new Point(2, 2));
        }

        var legendY = 4d;
        if (hasBase)
        {
            dc.DrawLine(_basePen, new Point(leftAxis + chartWidth - 170, legendY + 7), new Point(leftAxis + chartWidth - 155, legendY + 7));
            DrawText(dc, $"Base ({BaseReport!.SampleCount:N0})", new Point(leftAxis + chartWidth - 150, legendY));
            legendY += 16;
        }

        if (hasCompare)
        {
            dc.DrawLine(_comparePen, new Point(leftAxis + chartWidth - 170, legendY + 7), new Point(leftAxis + chartWidth - 155, legendY + 7));
            DrawText(dc, $"Compare ({CompareReport!.SampleCount:N0})", new Point(leftAxis + chartWidth - 150, legendY));
        }
    }

    private void DrawSeries(
        DrawingContext dc,
        IReadOnlyList<SignalSample> samples,
        long startNs,
        long durationNs,
        double minValue,
        double maxValue,
        double leftAxis,
        double chartWidth,
        double chartHeight,
        Pen pen)
    {
        var valueRange = Math.Max(1e-9, maxValue - minValue);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = true;
            foreach (var sample in samples)
            {
                var relativeNs = sample.TimestampNanoseconds - startNs;
                var x = leftAxis + Math.Clamp(relativeNs / (double)durationNs, 0, 1) * chartWidth;
                var y = 6 + chartHeight - (sample.Value - minValue) / valueRange * chartHeight;
                if (first)
                {
                    context.BeginFigure(new Point(x, y), false, false);
                    first = false;
                }
                else
                {
                    context.LineTo(new Point(x, y), true, false);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawCentered(DrawingContext dc, string text)
    {
        var formatted = new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, _mutedText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point((ActualWidth - formatted.Width) / 2, (ActualHeight - formatted.Height) / 2));
    }

    private void DrawText(DrawingContext dc, string text, Point point) =>
        dc.DrawText(
            new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, _mutedText, VisualTreeHelper.GetDpi(this).PixelsPerDip),
            point);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen DashedPen(Brush brush)
    {
        var pen = new Pen(brush, 1.75) { DashStyle = new DashStyle([5, 3], 0) };
        pen.Freeze();
        return pen;
    }
}
