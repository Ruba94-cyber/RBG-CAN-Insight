using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App.Controls;

/// <summary>
/// A single horizontal strip spanning the whole trace duration, with one tick mark per timing
/// incident positioned by when it happened and colored by severity. This is the "general, by
/// time" view of trace health: a glance at where problems cluster in the recording, as opposed
/// to the detailed grid below it, which is organized as a chronological list rather than grouped
/// by signal.
/// </summary>
public sealed class TraceHealthTimelineControl : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(TraceHealthTimelineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TraceStartNanosecondsProperty =
        DependencyProperty.Register(
            nameof(TraceStartNanoseconds),
            typeof(long),
            typeof(TraceHealthTimelineControl),
            new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TraceEndNanosecondsProperty =
        DependencyProperty.Register(
            nameof(TraceEndNanoseconds),
            typeof(long),
            typeof(TraceHealthTimelineControl),
            new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    private Brush _background = Frozen(0xF2, 0xF4, 0xF6);
    private Brush _axisBrush = Frozen(0xB8, 0xC0, 0xCB);
    private Brush _mutedText = Frozen(0x1F, 0x27, 0x33);
    private Brush _warnBrush = Frozen(212, 168, 55);
    private Brush _highBrush = Frozen(230, 126, 34);
    private Brush _critBrush = Frozen(224, 74, 74);

    public TraceHealthTimelineControl()
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
        _axisBrush = TryFindResource("BorderBrush") as Brush ?? _axisBrush;
        _mutedText = TryFindResource("CanvasTextBrush") as Brush ?? _mutedText;
        _warnBrush = TryFindResource("WarningBrush") as Brush ?? _warnBrush;
        _highBrush = TryFindResource("AccentBrush") as Brush ?? _highBrush;
        _critBrush = TryFindResource("ErrorBrush") as Brush ?? _critBrush;
        InvalidateVisual();
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public long TraceStartNanoseconds
    {
        get => (long)GetValue(TraceStartNanosecondsProperty);
        set => SetValue(TraceStartNanosecondsProperty, value);
    }

    public long TraceEndNanoseconds
    {
        get => (long)GetValue(TraceEndNanosecondsProperty);
        set => SetValue(TraceEndNanosecondsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(_background, null, new Rect(RenderSize));
        if (ActualWidth < 20 || ActualHeight < 20)
        {
            return;
        }

        const double axisY = 6;
        const double leftMargin = 4;
        var trackWidth = Math.Max(10, ActualWidth - leftMargin * 2);
        dc.DrawLine(new Pen(_axisBrush, 1), new Point(leftMargin, axisY), new Point(leftMargin + trackWidth, axisY));

        var durationNs = Math.Max(1, TraceEndNanoseconds - TraceStartNanoseconds);
        var rows = ItemsSource?.Cast<TraceHealthRowViewModel>().ToArray() ?? [];
        foreach (var row in rows)
        {
            var relative = Math.Clamp((row.TimestampNanoseconds - TraceStartNanoseconds) / (double)durationNs, 0, 1);
            var x = leftMargin + relative * trackWidth;
            var brush = row.Severity switch
            {
                "Crit" => _critBrush,
                "High" => _highBrush,
                _ => _warnBrush
            };
            var height = row.Severity switch
            {
                "Crit" => ActualHeight - 14,
                "High" => (ActualHeight - 14) * 0.75,
                _ => (ActualHeight - 14) * 0.5
            };
            dc.DrawLine(new Pen(brush, 2), new Point(x, axisY), new Point(x, axisY + height));
        }

        DrawText(dc, "0 s", new Point(leftMargin, ActualHeight - 12));
        var endLabel = (durationNs / 1_000_000_000d).ToString("F1", CultureInfo.InvariantCulture) + " s";
        var endFormatted = FormatText(endLabel);
        DrawText(dc, endLabel, new Point(leftMargin + trackWidth - endFormatted.Width, ActualHeight - 12));

        if (rows.Length == 0)
        {
            DrawCentered(dc, "No timing incidents to place on the timeline.");
        }
    }

    private FormattedText FormatText(string text) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10.5, _mutedText, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawText(DrawingContext dc, string text, Point point) => dc.DrawText(FormatText(text), point);

    private void DrawCentered(DrawingContext dc, string text)
    {
        var formatted = FormatText(text);
        dc.DrawText(formatted, new Point((ActualWidth - formatted.Width) / 2, (ActualHeight - formatted.Height) / 2));
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
