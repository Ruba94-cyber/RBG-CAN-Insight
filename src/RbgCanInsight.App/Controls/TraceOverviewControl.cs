using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RbgCanInsight.App.Common;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.Controls;

/// <summary>
/// Trace overview strip: a single message-density histogram over the whole loaded trace, with a
/// draggable replay cursor. Each bar's height is the total frame count in that time slice,
/// normalized to the busiest slice in the trace, so bursts and quiet periods read at a glance —
/// answering "when was traffic dense?" for the trace as a whole, not "which ID did what". This
/// mirrors the overview strip used elsewhere in the product line rather than showing a per-signal
/// breakdown, which belongs to the Trace grid and the Change/Byte Matrix views.
/// </summary>
public sealed class TraceOverviewControl : FrameworkElement
{
    private const int BucketCount = 240;
    private const double TopMargin = 2;
    private const double BottomMargin = 21;

    public static readonly DependencyProperty FramesProperty =
        DependencyProperty.Register(
            nameof(Frames),
            typeof(IEnumerable),
            typeof(TraceOverviewControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnFramesChanged));

    public static readonly DependencyProperty CurrentPercentProperty =
        DependencyProperty.Register(
            nameof(CurrentPercent),
            typeof(double),
            typeof(TraceOverviewControl),
            new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    private Brush _background = Frozen(0xF2, 0xF4, 0xF6);
    private Brush _barBrush = Frozen(0x2A, 0x78, 0xD6);
    private Brush _hotBarBrush = Frozen(0xC9, 0x43, 0x3F);
    private Brush _text = Frozen(0x1F, 0x27, 0x33);
    private Pen _grid = FrozenPen(Frozen(0xD8, 0xDE, 0xE6), 1);
    private Pen _cursor = FrozenPen(Frozen(0xC9, 0x43, 0x3F), 1.6);

    private long[] _bucketCounts = [];
    private long _maxBucketCount = 1;
    private long _hotThreshold = long.MaxValue;
    private long _cacheFirst;
    private long _cacheSpan;
    private long _totalFrames;
    private double _durationSeconds;
    private int _hoverBucket = -1;
    private ToolTip? _activeTooltip;
    private bool _isDragging;

    public TraceOverviewControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
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
        // The canvas is light in every theme, so bars/cursor take the light-canvas series inks
        // rather than the app-chrome accents that were tuned against a dark background.
        _background = TryFindResource("CanvasBackgroundBrush") as Brush ?? _background;
        _barBrush = TryFindResource("PlotSeries1Brush") as Brush ?? _barBrush;
        _hotBarBrush = TryFindResource("PlotSeries8Brush") as Brush ?? _hotBarBrush;
        _text = TryFindResource("CanvasTextBrush") as Brush ?? _text;
        _grid = FrozenPen(TryFindResource("CanvasGridLineBrush") as Brush ?? _grid.Brush, 1);
        _cursor = FrozenPen(TryFindResource("PlotSeries8Brush") as Brush ?? _cursor.Brush, 1.6);
        InvalidateVisual();
    }

    public IEnumerable? Frames
    {
        get => (IEnumerable?)GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
    }

    public double CurrentPercent
    {
        get => (double)GetValue(CurrentPercentProperty);
        set => SetValue(CurrentPercentProperty, Math.Clamp(value, 0, 100));
    }

    public event EventHandler<double>? PercentRequested;

    private static void OnFramesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TraceOverviewControl)d).RebuildHistogram();

    /// <summary>
    /// Bucketing runs once per trace load (not per paint): OnRender only draws from the cached
    /// counts, so dragging the replay cursor repaints without re-scanning every frame.
    /// </summary>
    private void RebuildHistogram()
    {
        var frames = Frames?.Cast<CanFrame>().ToArray() ?? [];
        if (frames.Length == 0)
        {
            _bucketCounts = [];
            _totalFrames = 0;
            InvalidateVisual();
            return;
        }

        var first = frames[0].TimestampNanoseconds;
        var last = frames[^1].TimestampNanoseconds;
        var span = Math.Max(1, last - first);

        var counts = new long[BucketCount];
        foreach (var frame in frames)
        {
            var bucket = (int)Math.Clamp((frame.TimestampNanoseconds - first) * (BucketCount - 1d) / span, 0, BucketCount - 1);
            counts[bucket]++;
        }

        _bucketCounts = counts;
        _maxBucketCount = Math.Max(1, counts.Max());
        var nonZeroSorted = counts.Where(c => c > 0).OrderBy(c => c).ToArray();
        _hotThreshold = nonZeroSorted.Length > 0
            ? Math.Max(2, nonZeroSorted[(int)(nonZeroSorted.Length * 0.75)])
            : long.MaxValue;
        _cacheFirst = first;
        _cacheSpan = span;
        _totalFrames = frames.Length;
        _durationSeconds = span / 1_000_000_000d;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(_background, null, new Rect(RenderSize));
        if (ActualWidth < 20 || ActualHeight < 20)
        {
            return;
        }

        if (_bucketCounts.Length == 0)
        {
            DrawText(dc, "No trace loaded", new Point(8, 7));
            return;
        }

        // A thin strip (the default) has no room for a caption row below the bars: the summary is drawn over
        // the top-left corner instead and the bars use the full height.
        var thin = ActualHeight < 64;
        var bottomMargin = thin ? 2 : BottomMargin;
        var usableHeight = Math.Max(1, ActualHeight - TopMargin - bottomMargin);
        var barWidth = ActualWidth / _bucketCounts.Length;

        dc.DrawLine(_grid, new Point(0, TopMargin), new Point(ActualWidth, TopMargin));

        for (var bucket = 0; bucket < _bucketCounts.Length; bucket++)
        {
            var count = _bucketCounts[bucket];
            if (count <= 0)
            {
                continue;
            }

            var height = Math.Max(1, count / (double)_maxBucketCount * usableHeight);
            var x = bucket * barWidth;
            var top = TopMargin + usableHeight - height;
            var brush = count >= _hotThreshold ? _hotBarBrush : _barBrush;
            dc.DrawRectangle(brush, null, new Rect(x, top, Math.Max(1, barWidth - 0.4), height));
        }

        var cursorX = Math.Clamp(CurrentPercent, 0, 100) / 100d * ActualWidth;
        dc.DrawLine(_cursor, new Point(cursorX, 0), new Point(cursorX, ActualHeight));

        DrawText(dc, $"{_totalFrames:N0} frames · {_durationSeconds:F3}s · peak {_maxBucketCount:N0} frame(s)/slice · hover for detail", new Point(6, thin ? 2 : ActualHeight - 19));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _isDragging = true;
        CaptureMouse();
        RequestPercent(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            RequestPercent(e.GetPosition(this).X);
            e.Handled = true;
        }

        UpdateHoverTooltip(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverBucket = -1;
        CloseActiveTooltip();
    }

    private void RequestPercent(double x)
    {
        var percent = Math.Clamp(x / Math.Max(1, ActualWidth) * 100d, 0, 100);
        CurrentPercent = percent;
        PercentRequested?.Invoke(this, percent);
    }

    private void UpdateHoverTooltip(double x)
    {
        if (_bucketCounts.Length == 0)
        {
            return;
        }

        var bucket = Math.Clamp((int)(x / Math.Max(1, ActualWidth) * _bucketCounts.Length), 0, _bucketCounts.Length - 1);
        if (bucket == _hoverBucket)
        {
            return;
        }

        _hoverBucket = bucket;
        var count = _bucketCounts[bucket];
        if (count <= 0)
        {
            CloseActiveTooltip();
            return;
        }

        var bucketStartNs = _cacheFirst + (long)(bucket * _cacheSpan / (double)_bucketCounts.Length);
        var bucketEndNs = _cacheFirst + (long)((bucket + 1) * _cacheSpan / (double)_bucketCounts.Length);
        var lines = new List<string>
        {
            $"t = {bucketStartNs / 1_000_000_000d:F3}s – {bucketEndNs / 1_000_000_000d:F3}s",
            $"{count:N0} frame(s) in this slice · {_totalFrames:N0} total in trace"
        };

        if (count >= _hotThreshold)
        {
            lines.Add("Dense burst relative to the rest of the trace.");
        }

        CloseActiveTooltip();
        var tip = new ToolTip
        {
            Content = string.Join(Environment.NewLine, lines),
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
            PlacementTarget = this,
            StaysOpen = true
        };
        ToolTip = tip;
        tip.IsOpen = true;
        _activeTooltip = tip;
    }

    private void CloseActiveTooltip()
    {
        if (_activeTooltip is not null)
        {
            _activeTooltip.IsOpen = false;
            _activeTooltip = null;
        }

        ToolTip = null;
    }

    private void DrawText(DrawingContext dc, string text, Point point) =>
        dc.DrawText(
            new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11.5,
                _text,
                VisualTreeHelper.GetDpi(this).PixelsPerDip),
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
}
