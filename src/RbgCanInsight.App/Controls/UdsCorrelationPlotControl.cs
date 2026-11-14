using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.Correlation;

namespace RbgCanInsight.App.Controls;

/// <summary>
/// Source-backed UDS/CAN time comparison. The X axis uses the ACTUAL
/// timestamp of each diagnostic and CAN sample, so a fitted lag remains
/// visible instead of drawing both curves at the DID time. Each Y series is
/// normalized independently for shape comparison; this does not prove that
/// the two physical quantities are identical.
/// </summary>
public sealed class UdsCorrelationPlotControl : FrameworkElement
{
    private const double LeftInset = 44d;
    private const double TopInset = 28d;
    private const double RightInset = 12d;
    private const double BottomInset = 30d;

    public static readonly DependencyProperty CorrelationProperty = DependencyProperty.Register(
        nameof(Correlation), typeof(UdsCanCorrelationResult), typeof(UdsCorrelationPlotControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            OnCorrelationChanged));

    public static readonly DependencyProperty SelectedPairIndexProperty = DependencyProperty.Register(
        nameof(SelectedPairIndex), typeof(int), typeof(UdsCorrelationPlotControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    private UdsCanCorrelationResult? _cachedCorrelation;
    private int _cachedPixelWidth = -1;
    private SignalSample[] _displayDid = [];
    private SignalSample[] _displayCan = [];
    private CorrelationPointHitIndex? _hitIndex;
    private long _startNs;
    private long _endNs;
    private double _didMin;
    private double _didMax;
    private double _canMin;
    private double _canMax;

    private static void OnCorrelationChanged(DependencyObject owner,
        DependencyPropertyChangedEventArgs args)
    {
        var chart = (UdsCorrelationPlotControl)owner;
        // A previous trace can hold tens of thousands of paired points.
        // Release cached arrays as soon as its selected correlation changes,
        // especially when another trace/DBC is imported.
        chart._cachedCorrelation = null;
        chart._cachedPixelWidth = -1;
        chart._displayDid = [];
        chart._displayCan = [];
        chart._hitIndex = null;
    }

    public UdsCanCorrelationResult? Correlation
    {
        get => (UdsCanCorrelationResult?)GetValue(CorrelationProperty);
        set => SetValue(CorrelationProperty, value);
    }

    public int SelectedPairIndex
    {
        get => (int)GetValue(SelectedPairIndexProperty);
        set => SetValue(SelectedPairIndexProperty, value);
    }

    /// <summary>Index in the original paired evidence, not a decimated
    /// display index. The host can highlight the same pair in its raw-frame
    /// inspector and jump to both source CAN frame indices.</summary>
    public event EventHandler<int>? PairSelected;

    public UdsCorrelationPlotControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
        Loaded += (_, _) => ThemeManager.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new CorrelationChartAutomationPeer(this);

    private sealed class CorrelationChartAutomationPeer : FrameworkElementAutomationPeer
    {
        public CorrelationChartAutomationPeer(UdsCorrelationPlotControl control) : base(control) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetClassNameCore() => nameof(UdsCorrelationPlotControl);
        protected override string GetLocalizedControlTypeCore() => "paired UDS and CAN time chart";
    }

    private void PrepareVisibleSeries(UdsCanCorrelationResult correlation, int width)
    {
        if (ReferenceEquals(_cachedCorrelation, correlation) && width == _cachedPixelWidth)
            return;

        var points = correlation.Points;
        _cachedCorrelation = correlation;
        _cachedPixelWidth = width;
        _hitIndex = new CorrelationPointHitIndex(points);
        if (points.Count == 0)
        {
            _displayDid = [];
            _displayCan = [];
            return;
        }

        // The trace may have been merged from multiple logger clocks; sort
        // the ORIGINAL observation timestamps before source-preserving
        // min/max/first/last downsampling. These arrays are cached across
        // selected-pair changes and remain bounded to screen pixels.
        (_startNs, _endNs) = CorrelationTimeAxis.Bounds(points);
        _didMin = points.Min(point => point.DidValue);
        _didMax = points.Max(point => point.DidValue);
        _canMin = points.Min(point => point.SignalValue);
        _canMax = points.Max(point => point.SignalValue);
        _displayDid = SignalWindowDecimator.Decimate(
            points.Select(point => new SignalSample(point.DidTimestampNanoseconds,
                point.DidValue, point.DidFrameIndex))
                .OrderBy(sample => sample.TimestampNanoseconds)
                .ThenBy(sample => sample.FrameIndex).ToArray(),
            _startNs, _endNs, width);
        _displayCan = SignalWindowDecimator.Decimate(
            points.Select(point => new SignalSample(point.SignalTimestampNanoseconds,
                point.SignalValue, point.SignalFrameIndex))
                .OrderBy(sample => sample.TimestampNanoseconds)
                .ThenBy(sample => sample.FrameIndex).ToArray(),
            _startNs, _endNs, width);
    }

    private double MapX(long timestamp)
    {
        var right = Math.Max(LeftInset + 1, ActualWidth - RightInset);
        return LeftInset + CorrelationTimeAxis.Fraction(timestamp, _startNs, _endNs) *
            (right - LeftInset);
    }

    private double MapY(double value, double minimum, double maximum)
    {
        var bottom = Math.Max(TopInset + 1, ActualHeight - BottomInset);
        var range = maximum - minimum;
        var normalized = range > 0 && double.IsFinite(range)
            ? (value - minimum) / range : 0.5d;
        if (!double.IsFinite(normalized)) normalized = 0.5d;
        return bottom - Math.Clamp(normalized, 0d, 1d) * (bottom - TopInset);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var background = TryFindResource("CanvasBackgroundBrush") as Brush ?? Brushes.White;
        var grid = TryFindResource("CanvasGridLineBrush") as Brush ?? Brushes.LightGray;
        var text = TryFindResource("CanvasTextBrush") as Brush ?? Brushes.DimGray;
        context.DrawRoundedRectangle(background, new Pen(grid, 1), new Rect(0.5, 0.5,
            Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), 4, 4);

        var points = Correlation?.Points;
        if (points is null || points.Count == 0 || ActualWidth < 120 || ActualHeight < 80)
        {
            DrawText(context, "No recorded DID/CAN source pairs for this hypothesis.",
                12, 10, 12, text);
            return;
        }

        PrepareVisibleSeries(Correlation!, Math.Max(1, (int)ActualWidth));
        var right = ActualWidth - RightInset;
        var bottom = ActualHeight - BottomInset;
        for (var index = 0; index <= 4; index++)
        {
            var y = TopInset + (bottom - TopInset) * index / 4d;
            context.DrawLine(new Pen(grid, 0.7), new Point(LeftInset, y), new Point(right, y));
        }

        var didBrush = TryFindResource("CorrelationDidBrush") as Brush ??
            new SolidColorBrush(Color.FromRgb(21, 85, 165));
        var canBrush = TryFindResource("CorrelationCanBrush") as Brush ??
            new SolidColorBrush(Color.FromRgb(153, 81, 0));
        DrawSeries(context, _displayDid, _didMin, _didMax, new Pen(didBrush, 1.7));
        DrawSeries(context, _displayCan, _canMin, _canMax, new Pen(canBrush, 1.7));

        // Highlight the exact full-resolution source observations, NEVER a
        // pixel-reduced proxy. The corresponding toolbar shows their frame
        // IDs and can open each in the raw Trace view.
        var selected = points[Math.Clamp(SelectedPairIndex, 0, points.Count - 1)];
        var didPosition = new Point(MapX(selected.DidTimestampNanoseconds),
            MapY(selected.DidValue, _didMin, _didMax));
        var canPosition = new Point(MapX(selected.SignalTimestampNanoseconds),
            MapY(selected.SignalValue, _canMin, _canMax));
        context.DrawEllipse(background, new Pen(didBrush, 2.5), didPosition, 5.5, 5.5);
        context.DrawEllipse(background, new Pen(canBrush, 2.5), canPosition, 5.5, 5.5);
        context.DrawLine(new Pen(grid, 1), didPosition, canPosition);

        DrawText(context, "● DID", 11, LeftInset, 2, didBrush);
        DrawText(context, "● CAN", 11, LeftInset + 66, 2, canBrush);
        // Keep legend readable in narrow chart columns: the evidence note
        // uses a separate line instead of extending beyond the plot canvas.
        DrawText(context, "Real timestamps · separate Y scales", 10,
            LeftInset, 15, text);
        DrawText(context, $"{_startNs / 1_000_000_000d:F3}s", 10, LeftInset, bottom + 5, text);
        DrawText(context, $"{_endNs / 1_000_000_000d:F3}s", 10,
            Math.Max(LeftInset, right - 56), bottom + 5, text);
    }

    private void DrawSeries(DrawingContext context, IReadOnlyList<SignalSample> points,
        double minimum, double maximum, Pen pen)
    {
        if (points.Count == 0) return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(MapX(points[0].TimestampNanoseconds),
                MapY(points[0].Value, minimum, maximum)), false, false);
            for (var i = 1; i < points.Count; i++)
            {
                path.LineTo(new Point(MapX(points[i].TimestampNanoseconds),
                    MapY(points[i].Value, minimum, maximum)), true, false);
            }
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);

        if (points.Count <= 40)
        {
            foreach (var point in points)
                context.DrawEllipse(pen.Brush, null,
                    new Point(MapX(point.TimestampNanoseconds),
                        MapY(point.Value, minimum, maximum)), 2, 2);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var count = Correlation?.Points.Count ?? 0;
        if (count == 0 || Keyboard.Modifiers != ModifierKeys.None)
            return;

        var current = Math.Clamp(SelectedPairIndex, 0, count - 1);
        var next = e.Key switch
        {
            Key.Left or Key.Up => Math.Max(0, current - 1),
            Key.Right or Key.Down => Math.Min(count - 1, current + 1),
            Key.Home => 0,
            Key.End => count - 1,
            _ => -1
        };
        if (next < 0) return;
        if (next != current)
            PairSelected?.Invoke(this, next);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var points = Correlation?.Points;
        if (points is null || points.Count == 0 || ActualWidth < 120)
            return;

        PrepareVisibleSeries(Correlation!, Math.Max(1, (int)ActualWidth));
        var click = e.GetPosition(this);
        // Binary search both source time series before testing marker geometry.
        // This avoids a full scan of every paired DID/CAN observation on
        // each click, without mapping hits to decimated substitute points.
        var nearest = _hitIndex?.FindNearest(click.X, click.Y,
            _startNs, _endNs, LeftInset, ActualWidth - RightInset,
            TopInset, ActualHeight - BottomInset,
            _didMin, _didMax, _canMin, _canMax) ?? -1;
        if (nearest < 0) return;
        Focus();
        PairSelected?.Invoke(this, nearest);
        e.Handled = true;
    }

    private void DrawText(DrawingContext context, string value, double size,
        double x, double y, Brush brush)
    {
        var formatted = new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(formatted, new Point(x, y));
    }
}
