using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App.Controls;

/// <summary>
/// Offline signal plot canvas: draws every plotted series over the full trace (or the current
/// zoom window), stacks digital/enum signals in their own lanes, and provides X1/X2 measurement
/// cursors with per-series readouts. Interactions: click = X1, Shift+click / right-click = X2,
/// double-click = clear cursors, Ctrl+click = seek the trace grid, mouse wheel = zoom,
/// Shift+wheel = pan.
/// </summary>
public sealed class SignalPlotControl : FrameworkElement
{
    private const double PlotEdgeInset = 6d;
    // DrawAxisLabels always pins the time-tick row to the bottom of the whole canvas; reserve that
    // strip up front so digital lanes (and the analog area itself) never get drawn underneath it.
    private const double TimeAxisLabelHeight = 18d;
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                OnItemsSourceChanged));

    /// <summary>
    /// When true, renders every series in <see cref="ItemsSource"/> regardless of
    /// <c>PlotSeriesViewModel.IsPlotted</c>. Used by a single-signal preview (e.g. a row's inline
    /// detail chart) that should show that one series on click, independent of whether the
    /// analyst has also added it to the persistent multi-signal plot.
    /// </summary>
    public static readonly DependencyProperty IgnorePlottedFilterProperty =
        DependencyProperty.Register(
            nameof(IgnorePlottedFilter),
            typeof(bool),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                OnIgnorePlottedFilterChanged));

    public static readonly DependencyProperty CursorTimestampNanosecondsProperty =
        DependencyProperty.Register(
            nameof(CursorTimestampNanoseconds),
            typeof(long),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeWindowSecondsProperty =
        DependencyProperty.Register(
            nameof(TimeWindowSeconds),
            typeof(double),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowEndTimestampNanosecondsProperty =
        DependencyProperty.Register(
            nameof(WindowEndTimestampNanoseconds),
            typeof(long),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CumulativeRevealProperty =
        DependencyProperty.Register(
            nameof(CumulativeReveal),
            typeof(bool),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Optional extent of the entire decoded offline recording,
    /// not merely the plotted series. A selected ECU may stop transmitting
    /// before other ECUs; the chart must still show the trailing evidence
    /// gap instead of clamping every cursor to that ECU's last frame.</summary>
    public static readonly DependencyProperty GlobalStartTimestampNanosecondsProperty =
        DependencyProperty.Register(nameof(GlobalStartTimestampNanoseconds), typeof(long),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlobalEndTimestampNanosecondsProperty =
        DependencyProperty.Register(nameof(GlobalEndTimestampNanoseconds), typeof(long),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 = Auto (per-signal scaling kicks in only when plotted units are mixed),
    /// 1 = always one shared Y axis, 2 = always one Y scale per analog series (each uses the full
    /// height, with its own min/max shown in the legend instead of on a shared axis — the
    /// CANalyzer-style behaviour a mixed-unit plot needs, since e.g. RPM and a temperature cannot
    /// share one scale without one of them reading as a flat line).</summary>
    public static readonly DependencyProperty YAxisModeIndexProperty =
        DependencyProperty.Register(
            nameof(YAxisModeIndex),
            typeof(int),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Controls optional on-canvas legend. The multi-signal workspace
    /// has a persistent, virtualized signal legend beneath the chart, so its
    /// default hides floating labels that can cover 20+ plotted curves.</summary>
    public static readonly DependencyProperty ShowInlineLegendProperty =
        DependencyProperty.Register(nameof(ShowInlineLegend), typeof(bool),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>False = last observed CAN sample is held until the next
    /// recorded one; true = optional visual straight-line interpolation.
    /// A linear segment never represents an additional source frame.</summary>
    public static readonly DependencyProperty LinearInterpolationProperty =
        DependencyProperty.Register(nameof(LinearInterpolation), typeof(bool),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Trace bookmark timestamps, drawn as thin marker lines so the plot shows where the
    /// user has already flagged something without switching back to the Trace view.</summary>
    public static readonly DependencyProperty MarkerTimestampsProperty =
        DependencyProperty.Register(
            nameof(MarkerTimestamps),
            typeof(IReadOnlyList<long>),
            typeof(SignalPlotControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
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
        var pen = new Pen(brush, 1.25) { DashStyle = new DashStyle([4, 3], 0) };
        pen.Freeze();
        return pen;
    }

    // Fallback = the validated categorical order for the light canvas (see Palette.*.xaml).
    private static readonly Brush[] DefaultSeriesBrushes =
    [
        Frozen(0x2A, 0x78, 0xD6),
        Frozen(0x00, 0x83, 0x00),
        Frozen(0xE8, 0x7B, 0xA4),
        Frozen(0xED, 0xA1, 0x00),
        Frozen(0x1B, 0xAF, 0x7A),
        Frozen(0xEB, 0x68, 0x34),
        Frozen(0x4A, 0x3A, 0xA7),
        Frozen(0xE3, 0x49, 0x48)
    ];

    private static readonly Brush TransparentSwatch = CreateTransparent();

    private static Brush CreateTransparent()
    {
        var brush = new SolidColorBrush(Colors.Transparent);
        brush.Freeze();
        return brush;
    }

    private readonly HashSet<PlotSeriesViewModel> _observedSeries = [];
    private INotifyCollectionChanged? _observedCollection;
    // The canvas is light in every theme (see Palette.*.xaml), so cursor/marker inks are fixed
    // dark tones rather than theme accents that were tuned for the dark app chrome.
    private Brush _backgroundBrush = Frozen(0xF2, 0xF4, 0xF6);
    private Brush _labelBackgroundBrush = Frozen(0xFF, 0xFF, 0xFF);
    private Brush _mutedTextBrush = Frozen(0x1F, 0x27, 0x33);
    private Pen _gridPen = FrozenPen(Frozen(0xD8, 0xDE, 0xE6), 1);
    private Brush[] _seriesBrushes = DefaultSeriesBrushes;
    private Pen[] _seriesPens = DefaultSeriesBrushes.Select(brush => FrozenPen(brush, 1.5)).ToArray();
    private readonly Pen _traceCursorPen = FrozenPen(Frozen(0x1E, 0x6F, 0xD0), 1.5);
    private readonly Pen _measure1Pen = DashedPen(Frozen(0xB4, 0x79, 0x0E));
    private readonly Pen _measure2Pen = DashedPen(Frozen(0x0E, 0x8A, 0x70));
    private readonly Pen _markerPen = DashedPen(Frozen(0x8E, 0x24, 0xAA));

    // Measurement cursors live in trace time so they stay glued to the data while zooming.
    private long _measure1Ns = -1;
    private long _measure2Ns = -1;

    // Zoom window in trace time; both -1 = full trace.
    private long _viewStartNs = -1;
    private long _viewEndNs = -1;

    private long _lastMinTime;
    private long _lastMaxTime;
    private bool _hasDataExtent;
    private (PlotSeriesViewModel Row, SignalSample[] Points)[] _lastSeries = [];
    // Cursor/marker repainting is frequent, but the visible time window and
    // its per-pixel source extrema often do not change. Cache only ONE window
    // per canvas to avoid repeated full-trace scans without unbounded memory.
    private PlotSeriesViewModel[] _cachedRows = [];
    private (PlotSeriesViewModel Row, SignalSample[] Points)[] _cachedWindowSeries = [];
    private (PlotSeriesViewModel Row, SignalSample[] Points)[] _cachedChangeDots = [];
    private PlotSeriesViewModel[] _selectedRows = [];
    private bool _selectedRowsDirty = true;
    private PlotSeriesViewModel[] _swatchRows = [];
    private Brush[]? _swatchPalette;
    private long _cachedStartNs = long.MinValue;
    private long _cachedEndNs = long.MinValue;
    private int _cachedPixelWidth = -1;
    private readonly List<(Rect Bounds, PlotSeriesViewModel Row)> _legendHitBoxes = [];

    // Pointer inspection always reads ACTUAL original CAN samples rather than
    // drawing-decimated points; keep only layout geometry, never per-frame UI
    // objects or another copy of the complete recording.
    private sealed record HoverLane(PlotSeriesViewModel Row, double Top,
        double Height, double Min, double Range);
    private readonly List<HoverLane> _hoverLanes = [];
    private Point? _hoverPoint;
    private PlotSeriesViewModel? _hoverRow;
    private PlotSampleInspection? _hoverEvidence;
    private string? _hoverTooltipText;
    private object? _priorToolTip;
    private object? _hoverFallbackToolTip;
    private readonly Pen _hoverPen = DashedPen(Frozen(0x65, 0x71, 0x7E));

    private void SetHoverFallbackToolTip(string? text)
    {
        if (string.Equals(_hoverTooltipText, text, StringComparison.Ordinal)) return;
        _hoverTooltipText = text;
        if (text is null)
        {
            if (_hoverFallbackToolTip is not null &&
                ReferenceEquals(ToolTip, _hoverFallbackToolTip))
                SetCurrentValue(ToolTipProperty, _priorToolTip);
            _hoverFallbackToolTip = null;
            return;
        }

        if (_hoverFallbackToolTip is null)
            _priorToolTip = ToolTip;
        var fallback = new System.Windows.Controls.TextBlock
        {
            Text = text,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            MaxWidth = 600
        };
        _hoverFallbackToolTip = fallback;
        SetCurrentValue(ToolTipProperty, fallback);
    }

    private void ClearHover()
    {
        _hoverPoint = null;
        _hoverRow = null;
        _hoverEvidence = null;
        SetHoverFallbackToolTip(null);
        _hoverLanes.Clear();
    }

    private void ClearWindowCache()
    {
        _cachedRows = [];
        _cachedWindowSeries = [];
        _cachedChangeDots = [];
        _cachedPixelWidth = -1;
        _lastSeries = [];
        _hasDataExtent = false;
        _selectedRowsDirty = true;
        _swatchRows = [];
        _swatchPalette = null;
        ClearHover();
    }

    private (PlotSeriesViewModel Row, SignalSample[] Points)[] GetWindowSeries(
        PlotSeriesViewModel[] rows, long minTime, long maxTime, int pixelWidth)
    {
        if (_cachedPixelWidth == pixelWidth &&
            _cachedStartNs == minTime && _cachedEndNs == maxTime &&
            _cachedRows.Length == rows.Length && _cachedRows.SequenceEqual(rows))
            return _cachedWindowSeries;

        var series = rows.Select(row => (
                Row: row,
                Points: SignalWindowDecimator.Decimate(row.Samples, minTime, maxTime, pixelWidth)))
            .Where(item => item.Points.Length > 0)
            .ToArray();
        _cachedRows = rows;
        _cachedStartNs = minTime;
        _cachedEndNs = maxTime;
        _cachedPixelWidth = pixelWidth;
        _cachedWindowSeries = series;
        // Compute from decimated original-frame samples once per visible
        // time window, not on every hover/cursor repaint. At most four
        // curves × 24 actual changes keep the plot calm on a 13-inch screen.
        _cachedChangeDots = series.Take(4)
            .Select(item => (item.Row,
                Points: PlotSampleInspector.ChangeDots(
                    item.Row.Samples, item.Points, maxDots: 24)))
            .Where(item => item.Points.Length > 0).ToArray();
        return series;
    }

    /// <summary>Raised on Ctrl+Click with the clicked timestamp so the host can seek the trace.</summary>
    public event EventHandler<long>? SeekRequested;

    /// <summary>Raised when Ctrl+click can identify an actual original CAN
    /// frame of the hovered decoded signal. Caller navigates by FRAME INDEX,
    /// not timestamp, so independent loggers with overlapping clocks cannot
    /// silently select the wrong recording.</summary>
    public event EventHandler<int>? OriginalFrameRequested;

    /// <summary>Raised on an ordinary X1 click, to synchronize the shared
    /// trace-time cursor without jumping the source frame selection. The X2
    /// measurement cursor is intentionally independent.</summary>
    public event EventHandler<long>? CursorSelected;

    /// <summary>Raised whenever this control's own zoom/pan changes (user gesture or reset), so a
    /// host can broadcast it to other plot windows via <see cref="PlotSyncBroker"/>. Not raised by
    /// <see cref="SetViewRange"/>, which is how an incoming broadcast is applied — this is what
    /// keeps multiple synced windows from echoing the same change back and forth.</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>Current trace-time zoom window; both -1 means "full trace".</summary>
    public (long Start, long End) ViewRange => (_viewStartNs, _viewEndNs);

    /// <summary>Applies a zoom window from an external source (e.g. another synced plot window)
    /// without raising <see cref="ZoomChanged"/> again.</summary>
    public void SetViewRange(long startNs, long endNs)
    {
        _viewStartNs = startNs;
        _viewEndNs = endNs;
        InvalidateVisual();
    }

    /// <summary>Expose this custom-drawn plot to UI Automation so screen
    /// readers and FlaUI can locate its time canvas. No fake button patterns
    /// are advertised: pointer gestures remain explicit.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new SignalPlotAutomationPeer(this);

    private sealed class SignalPlotAutomationPeer : FrameworkElementAutomationPeer
    {
        public SignalPlotAutomationPeer(SignalPlotControl owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetClassNameCore() => nameof(SignalPlotControl);
        protected override string GetLocalizedControlTypeCore() => "offline CAN signal plot";
    }

    public SignalPlotControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Loaded += (_, _) =>
        {
            ResolveThemeBrushes();
            SubscribeToSeries(ItemsSource);
            ThemeManager.ThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            UnsubscribeFromSeries();
            ClearWindowCache();
            ThemeManager.ThemeChanged -= OnThemeChanged;
        };
    }

    private static void OnIgnorePlottedFilterChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var plot = (SignalPlotControl)sender;
        plot.ClearWindowCache();
        plot.InvalidateVisual();
    }

    private static void OnItemsSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var plot = (SignalPlotControl)sender;
        plot.UnsubscribeFromSeries();
        plot.ClearWindowCache();
        if (plot.IsLoaded)
            plot.SubscribeToSeries(e.NewValue as IEnumerable);
        plot.InvalidateVisual();
    }

    private void SubscribeToSeries(IEnumerable? items)
    {
        if (items is null) return;
        if (items is INotifyCollectionChanged collection)
        {
            _observedCollection = collection;
            collection.CollectionChanged += OnSeriesCollectionChanged;
        }
        RefreshSeriesSubscriptions(items);
    }

    private void OnSeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ResettableObservableCollection.ReplaceAll raises Reset without a
        // separate ItemsSource DP change. Rewire on every collection mutation,
        // otherwise the chart continues observing stale series after trace load.
        RefreshSeriesSubscriptions(ItemsSource);
        ClearWindowCache();
        InvalidateVisual();
    }

    private void RefreshSeriesSubscriptions(IEnumerable? items)
    {
        var current = items?.OfType<PlotSeriesViewModel>().ToHashSet() ?? [];
        foreach (var removed in _observedSeries.Where(row => !current.Contains(row)).ToArray())
        {
            removed.PropertyChanged -= OnSeriesPropertyChanged;
            _observedSeries.Remove(removed);
        }
        foreach (var added in current)
        {
            if (_observedSeries.Add(added))
                added.PropertyChanged += OnSeriesPropertyChanged;
        }
    }

    private void UnsubscribeFromSeries()
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= OnSeriesCollectionChanged;
            _observedCollection = null;
        }
        foreach (var row in _observedSeries)
            row.PropertyChanged -= OnSeriesPropertyChanged;
        _observedSeries.Clear();
    }

    private void OnSeriesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlotSeriesViewModel.IsPlotted))
        {
            _selectedRowsDirty = true;
            InvalidateVisual();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ResolveThemeBrushes();

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool IgnorePlottedFilter
    {
        get => (bool)GetValue(IgnorePlottedFilterProperty);
        set => SetValue(IgnorePlottedFilterProperty, value);
    }

    /// <summary>Trace-sync cursor timestamp; -1 hides the cursor.</summary>
    public long CursorTimestampNanoseconds
    {
        get => (long)GetValue(CursorTimestampNanosecondsProperty);
        set => SetValue(CursorTimestampNanosecondsProperty, value);
    }

    /// <summary>Visible window size in seconds. 0 means fit all unless the user wheel-zooms.</summary>
    public double TimeWindowSeconds
    {
        get => (double)GetValue(TimeWindowSecondsProperty);
        set => SetValue(TimeWindowSecondsProperty, value);
    }

    /// <summary>Right edge of the offline plot window. -1 means tail of the loaded data.</summary>
    public long WindowEndTimestampNanoseconds
    {
        get => (long)GetValue(WindowEndTimestampNanosecondsProperty);
        set => SetValue(WindowEndTimestampNanosecondsProperty, value);
    }

    /// <summary>When true, playback reveals samples from the first timestamp up to the current
    /// window end instead of sliding a fixed-size time window.</summary>
    public bool CumulativeReveal
    {
        get => (bool)GetValue(CumulativeRevealProperty);
        set => SetValue(CumulativeRevealProperty, value);
    }

    public long GlobalStartTimestampNanoseconds
    {
        get => (long)GetValue(GlobalStartTimestampNanosecondsProperty);
        set => SetValue(GlobalStartTimestampNanosecondsProperty, value);
    }

    public long GlobalEndTimestampNanoseconds
    {
        get => (long)GetValue(GlobalEndTimestampNanosecondsProperty);
        set => SetValue(GlobalEndTimestampNanosecondsProperty, value);
    }

    public int YAxisModeIndex
    {
        get => (int)GetValue(YAxisModeIndexProperty);
        set => SetValue(YAxisModeIndexProperty, value);
    }

    public bool ShowInlineLegend
    {
        get => (bool)GetValue(ShowInlineLegendProperty);
        set => SetValue(ShowInlineLegendProperty, value);
    }

    public bool LinearInterpolation
    {
        get => (bool)GetValue(LinearInterpolationProperty);
        set => SetValue(LinearInterpolationProperty, value);
    }

    public IReadOnlyList<long>? MarkerTimestamps
    {
        get => (IReadOnlyList<long>?)GetValue(MarkerTimestampsProperty);
        set => SetValue(MarkerTimestampsProperty, value);
    }

    public void ResetZoom()
    {
        _viewStartNs = -1;
        _viewEndNs = -1;
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResolveThemeBrushes()
    {
        _backgroundBrush = Resolve("CanvasBackgroundBrush", _backgroundBrush);
        _labelBackgroundBrush = Resolve("CanvasLabelBrush", _labelBackgroundBrush);
        _mutedTextBrush = Resolve("CanvasTextBrush", _mutedTextBrush);
        _gridPen = FrozenPen(Resolve("CanvasGridLineBrush", _gridPen.Brush), 1);
        var series = new Brush[8];
        var missing = false;
        for (var index = 0; index < series.Length; index++)
        {
            var themed = TryFindResource($"PlotSeries{index + 1}Brush") as Brush;
            missing |= themed is null;
            series[index] = themed ?? DefaultSeriesBrushes[index];
        }

        _seriesBrushes = missing ? DefaultSeriesBrushes : series;
        _seriesPens = _seriesBrushes.Select(brush => FrozenPen(brush, 1.5)).ToArray();
        InvalidateVisual();
    }

    private Brush Resolve(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(_backgroundBrush, null, new Rect(RenderSize));
        if (ActualWidth < 20 || ActualHeight < 20)
        {
            return;
        }

        // A multi-DBC catalog can have thousands of observed signals but
        // only a handful plotted. Cursor updates do not change that set:
        // calculate the selected rows only on selection/collection changes.
        if (_selectedRowsDirty)
        {
            _selectedRows = ItemsSource?.OfType<PlotSeriesViewModel>()
                .Where(row => IgnorePlottedFilter || row.IsPlotted)
                .ToArray() ?? [];
            _selectedRowsDirty = false;
        }
        var rows = _selectedRows;

        var fullMin = long.MaxValue;
        var fullMax = long.MinValue;
        foreach (var row in rows)
        {
            var samples = row.Samples;
            if (samples.Count == 0)
            {
                continue;
            }

            fullMin = Math.Min(fullMin, samples[0].TimestampNanoseconds);
            fullMax = Math.Max(fullMax, samples[^1].TimestampNanoseconds);
        }

        if (fullMin > fullMax)
        {
            ClearHover();
            _hasDataExtent = false;
            _lastSeries = [];
            PublishSwatches(rows);
            _legendHitBoxes.Clear();
            DrawCenteredText(dc,
                rows.Length == 0
                    ? "No signals plotted.\nTick Plot on a signal in the list below,\nor send signals here from the Change Matrix."
                    : "The selected signals have no decoded samples in this trace.");
            return;
        }

        if (GlobalStartTimestampNanoseconds >= 0 &&
            GlobalEndTimestampNanoseconds >= GlobalStartTimestampNanoseconds)
        {
            fullMin = Math.Min(fullMin, GlobalStartTimestampNanoseconds);
            fullMax = Math.Max(fullMax, GlobalEndTimestampNanoseconds);
        }

        var minTime = fullMin;
        var maxTime = fullMax;
        if (_viewStartNs >= 0 && _viewEndNs > _viewStartNs)
        {
            minTime = Math.Max(_viewStartNs, fullMin);
            maxTime = Math.Min(_viewEndNs, fullMax);
        }
        else if (CumulativeReveal && WindowEndTimestampNanoseconds >= 0)
        {
            maxTime = Math.Clamp(WindowEndTimestampNanoseconds, fullMin, fullMax);
            minTime = fullMin;
        }
        else if (TimeWindowSeconds > 0)
        {
            maxTime = WindowEndTimestampNanoseconds >= 0
                ? Math.Clamp(WindowEndTimestampNanoseconds, fullMin, fullMax)
                : fullMax;
            minTime = Math.Max(fullMin, maxTime - (long)(TimeWindowSeconds * 1_000_000_000d));
            if (maxTime <= minTime && maxTime <= fullMin)
            {
                minTime = fullMin;
                maxTime = Math.Min(fullMax, fullMin + (long)(TimeWindowSeconds * 1_000_000_000d));
            }
        }
        if (maxTime <= minTime)
        {
            minTime = fullMin;
            maxTime = fullMax;
        }

        var timeRange = Math.Max(1, maxTime - minTime);
        _hasDataExtent = true;
        _lastMinTime = minTime;
        _lastMaxTime = maxTime;

        // Reuse the most recent per-pixel extrema when only the cursor,
        // measurement markers, plot labels or theme changes. A fresh scroll,
        // zoom, resize, selection or trace import always gets new evidence.
        var series = GetWindowSeries(rows, minTime, maxTime, Math.Max(64, (int)ActualWidth));
        _lastSeries = series;
        PublishSwatches(rows);

        if (series.Length == 0)
        {
            _hoverLanes.Clear();
            _legendHitBoxes.Clear();
            var held = rows.Count(row => row.Timeline.SampleAt(minTime) is not null);
            DrawCenteredText(dc,
                "No NEW decoded CAN samples in this time window.\n" +
                $"{held} of {rows.Length} plotted signals have a value from an earlier frame (held, not freshly observed).\n" +
                "Select X1 to inspect a time, or zoom out to find recorded transitions.");

            // The time range remains real even when no plotted ECU sent a
            // new sample. Keep both the time axis and X1/X2 cursors usable
            // so an analyst can compare held values without inventing frames.
            DrawAxisLabels(dc, minTime, maxTime, 0, 1, string.Empty,
                ActualHeight - TimeAxisLabelHeight, showValueTicks: false);
            DrawMarkers(dc, minTime, maxTime, timeRange);
            DrawTraceCursor(dc, minTime, timeRange);
            DrawMeasurementCursors(dc, rows
                .Select(row => (Row: row, Points: Array.Empty<SignalSample>()))
                .ToArray(), minTime, timeRange);
            return;
        }

        // Only DBC-declared digital/enum/low-bit-width signals use logic
        // lanes. A numerical series with two values in the current zoom
        // remains analog instead of changing shape each time users zoom.
        var isStep = series.Select(item => item.Row.IsDigitalSignal).ToArray();
        var digitalCount = isStep.Count(flag => flag);
        var allDigital = digitalCount == series.Length;
        var useLanes = digitalCount > 0;
        // Pure discrete signals use the entire canvas as logic lanes.
        // Previously they fell through to analog interpolation whenever
        // no analog curve was selected.
        var analogHeight = allDigital
            ? 0d
            : useLanes
                ? Math.Max(40, ActualHeight - TimeAxisLabelHeight - Math.Min(ActualHeight * 0.4, digitalCount * 34 + 6))
                : Math.Max(40, ActualHeight - TimeAxisLabelHeight);

        if (!allDigital)
        {
            for (var index = 1; index < 5; index++)
            {
                var y = analogHeight * index / 5;
                dc.DrawLine(_gridPen, new Point(0, y), new Point(ActualWidth, y));
            }
        }

        // Vertical time grid so a value can be placed in time without cursor gymnastics.
        for (var index = 1; index < TimeDivisions; index++)
        {
            var x = ActualWidth * index / TimeDivisions;
            dc.DrawLine(_gridPen, new Point(x, 0), new Point(x, allDigital ? ActualHeight - TimeAxisLabelHeight : analogHeight));
        }

        var scaleSeries = useLanes
            ? series.Where((_, index) => !isStep[index]).ToArray()
            : series;
        var analogUnits = scaleSeries
            .Select(item => item.Row.Unit)
            .Where(unit => !string.IsNullOrWhiteSpace(unit))
            .Distinct()
            .ToArray();
        // Auto: a single shared axis reads as a flat line the moment two very different physical
        // ranges (e.g. RPM and a temperature) share it, so mixed units switch to a per-series scale
        // by default; a single unit keeps the familiar shared axis. Explicit modes always win.
        var perSignalScale = YAxisModeIndex switch
        {
            1 => false,
            2 => true,
            _ => analogUnits.Length > 1
        };

        (double MinValue, double ValueRange) PaddedRange(IEnumerable<(PlotSeriesViewModel Row, SignalSample[] Points)> items)
        {
            var dataMin = items.Min(item => item.Points.Min(point => point.Value));
            var dataMax = items.Max(item => item.Points.Max(point => point.Value));
            var range = dataMax - dataMin;
            var padding = range > 1e-12 ? range * 0.06 : Math.Max(0.5, Math.Abs(dataMin) * 0.06);
            // Never map a real sample exactly onto y=0 or y=height: half of the stroke would be
            // clipped by ClipToBounds and the upper/lower envelope would look missing.
            var min = dataMin - padding;
            var max = dataMax + padding;
            return (min, Math.Max(1e-9, max - min));
        }

        var (sharedMinValue, sharedValueRange) = scaleSeries.Length > 0
            ? PaddedRange(scaleSeries)
            : (0d, 1d);
        var sharedMaxValue = sharedMinValue + sharedValueRange;

        var laneIndex = 0;
        _hoverLanes.Clear();
        var anyFullScale = false;
        for (var seriesIndex = 0; seriesIndex < series.Length; seriesIndex++)
        {
            var pen = _seriesPens[seriesIndex % _seriesPens.Length];
            if (useLanes && isStep[seriesIndex])
            {
                var laneHeight = (ActualHeight - TimeAxisLabelHeight - analogHeight - 2) / digitalCount;
                var laneTop = analogHeight + 6 + laneIndex * laneHeight;
                var laneMin = series[seriesIndex].Points.Min(point => point.Value);
                var laneRange = Math.Max(1e-9,
                    series[seriesIndex].Points.Max(point => point.Value) - laneMin);
                _hoverLanes.Add(new HoverLane(series[seriesIndex].Row, laneTop,
                    Math.Max(4, laneHeight - 8), laneMin, laneRange));
                DrawDigitalLane(dc, series[seriesIndex], pen, minTime, timeRange, analogHeight, laneIndex++, digitalCount);
            }
            else
            {
                var (seriesMin, seriesRange) = perSignalScale
                    ? PaddedRange([series[seriesIndex]])
                    : (sharedMinValue, sharedValueRange);
                _hoverLanes.Add(new HoverLane(series[seriesIndex].Row, 0,
                    analogHeight, seriesMin, seriesRange));
                anyFullScale |= DrawAnalogSeries(dc, series[seriesIndex], pen, minTime, timeRange, seriesMin, seriesRange, analogHeight);
            }
        }

        if (useLanes && !allDigital)
        {
            dc.DrawLine(_gridPen, new Point(0, analogHeight), new Point(ActualWidth, analogHeight));
        }
        DrawRecordedChangeDots(dc, minTime, timeRange);

        var yUnit = analogUnits.Length switch
        {
            1 => analogUnits[0],
            > 1 when !perSignalScale => "mixed units",
            _ => string.Empty
        };

        DrawAxisLabels(dc, minTime, maxTime, sharedMinValue, sharedMaxValue, yUnit, analogHeight, showValueTicks: !perSignalScale && !allDigital);
        if (ShowInlineLegend)
            DrawLegend(dc, series, perSignalScale);
        else
            _legendHitBoxes.Clear();
        DrawMarkers(dc, minTime, maxTime, timeRange);
        DrawTraceCursor(dc, minTime, timeRange);
        DrawMeasurementCursors(dc, series, minTime, timeRange);
        DrawHoverEvidence(dc, minTime, timeRange);
        var hintY = 4d;
        if (_hoverEvidence is null)
        {
            dc.DrawText(SmallText("○ = source change (sampled) · hover: time, value, Δ, frame"),
                new Point(46, hintY));
            hintY += 16;
        }
        if (_viewStartNs >= 0)
        {
            dc.DrawText(SmallText("zoomed — Ctrl+0 or button to reset"), new Point(46, hintY));
            hintY += 16;
        }

        if (IsKeyboardFocused && _activeRow is not null)
        {
            dc.DrawText(SmallText(
                $"active: {_activeRow.SignalName} · line: {PlotCursorMath.StyleLabel(_activeRow.LineStyle)} · " +
                "Tab signal · L line style · Shift/Ctrl+←→ X1/X2 · Shift/Ctrl+↑↓ sample"), new Point(46, hintY));
            hintY += 16;
        }

        if (anyFullScale)
        {
            dc.DrawText(
                SmallText("○ all-ones raw code — check DBC semantics; may also be a valid value"),
                new Point(46, hintY));
            hintY += 16;
        }

        var withoutNewFrames = rows.Length - series.Length;
        if (withoutNewFrames > 0)
        {
            dc.DrawText(SmallText(
                $"{withoutNewFrames} plotted signal(s) have no NEW decoded sample in this window"),
                new Point(46, hintY));
        }
    }

    private const int TimeDivisions = 6;

    private void PublishSwatches(PlotSeriesViewModel[] rows)
    {
        if (IgnorePlottedFilter)
            return;

        // Updating every unselected signal's swatch on every playback tick
        // would scan the entire catalog, even when its selection and palette
        // are unchanged. Publish once per selection or theme change instead.
        if (ReferenceEquals(_swatchPalette, _seriesBrushes) &&
            _swatchRows.Length == rows.Length && _swatchRows.SequenceEqual(rows))
            return;

        var colorByRow = new Dictionary<PlotSeriesViewModel, Brush>();
        for (var index = 0; index < rows.Length; index++)
        {
            colorByRow[rows[index]] = _seriesBrushes[index % _seriesBrushes.Length];
        }

        if (ItemsSource is null)
        {
            return;
        }

        foreach (var row in ItemsSource.OfType<PlotSeriesViewModel>())
        {
            var target = colorByRow.TryGetValue(row, out var brush) ? brush : TransparentSwatch;
            if (!ReferenceEquals(row.PlotColor, target))
                row.PlotColor = target;
        }
        _swatchRows = rows;
        _swatchPalette = _seriesBrushes;
    }

     /// <summary>Draws one analog series and returns true when any visible sample sits at the
    /// signal's raw all-ones pattern (drawn as hollow circles, without inferring invalidity),
    /// reading is never mistaken for a real measurement).</summary>
    private bool DrawAnalogSeries(
        DrawingContext dc,
        (PlotSeriesViewModel Row, SignalSample[] Points) series,
        Pen pen,
        long minTime,
        long timeRange,
        double minValue,
        double valueRange,
        double regionHeight)
    {
        var (row, points) = series;
        var style = row.LineStyle == PlotLineStyle.Auto
            ? (LinearInterpolation ? PlotLineStyle.Line : PlotLineStyle.Steps)
            : row.LineStyle;

        double ToX(SignalSample point) => TimestampToX(point.TimestampNanoseconds, minTime, timeRange);
        double ToY(SignalSample point) => regionHeight - ((point.Value - minValue) * regionHeight / valueRange);

        if (points.Length == 1)
        {
            var point = points[0];
            dc.DrawEllipse(pen.Brush, null, new Point(ToX(point), ToY(point)), 3.5, 3.5);
            return DrawFullScaleMarkers(dc, row, points, pen, ToX, ToY);
        }

        if (style == PlotLineStyle.Dots)
        {
            foreach (var point in points)
                dc.DrawEllipse(pen.Brush, null, new Point(ToX(point), ToY(point)), 2.5, 2.5);
            return DrawFullScaleMarkers(dc, row, points, pen, ToX, ToY);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = true;
            var previousY = 0d;
            foreach (var point in points)
            {
                var x = ToX(point);
                var y = ToY(point);
                if (first)
                {
                    context.BeginFigure(new Point(x, y), false, false);
                    first = false;
                }
                else
                {
                    if (style == PlotLineStyle.Steps)
                    {
                        // An ECU only reported the previous value until this
                        // sample arrived. Drawing a diagonal would imply
                        // intermediate measurements that never occurred.
                        context.LineTo(new Point(x, previousY), true, false);
                    }
                    context.LineTo(new Point(x, y), true, false);
                }
                previousY = y;
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
        return DrawFullScaleMarkers(dc, row, points, pen, ToX, ToY);
    }

    /// <summary>Hollow circles over full-scale samples; thinned so a long saturated stretch does
    /// not turn into a solid band of markers.</summary>
    private bool DrawFullScaleMarkers(
        DrawingContext dc,
        PlotSeriesViewModel row,
        SignalSample[] points,
        Pen pen,
        Func<SignalSample, double> toX,
        Func<SignalSample, double> toY)
    {
        if (row.FullScaleValue is null)
        {
            return false;
        }

        var saturatedCount = 0;
        foreach (var point in points)
        {
            if (row.IsFullScale(point.Value))
            {
                saturatedCount++;
            }
        }

        if (saturatedCount == 0)
        {
            return false;
        }

        var maxMarkers = Math.Max(24, (int)(ActualWidth / 12));
        var stride = Math.Max(1, saturatedCount / maxMarkers);
        var seen = 0;
        foreach (var point in points)
        {
            if (!row.IsFullScale(point.Value))
            {
                continue;
            }

            if (seen++ % stride == 0)
            {
                dc.DrawEllipse(_backgroundBrush, pen, new Point(toX(point), toY(point)), 3, 3);
            }
        }

        return true;
    }

    private void DrawDigitalLane(
        DrawingContext dc,
        (PlotSeriesViewModel Row, SignalSample[] Points) series,
        Pen pen,
        long minTime,
        long timeRange,
        double lanesTop,
        int laneIndex,
        int laneCount)
    {
        var lanesHeight = ActualHeight - TimeAxisLabelHeight - lanesTop - 2;
        var laneHeight = lanesHeight / laneCount;
        var top = lanesTop + 2 + laneIndex * laneHeight;
        var usable = Math.Max(4, laneHeight - 8);
        var points = series.Points;
        var minimum = points.Min(point => point.Value);
        var maximum = points.Max(point => point.Value);
        var range = Math.Max(1e-9, maximum - minimum);

        double ValueToY(double value) => top + 4 + usable - ((value - minimum) / range * usable);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = true;
            var lastY = 0d;
            foreach (var point in points)
            {
                var x = TimestampToX(point.TimestampNanoseconds, minTime, timeRange);
                var y = ValueToY(point.Value);
                if (first)
                {
                    context.BeginFigure(new Point(x, y), false, false);
                    first = false;
                }
                else
                {
                    context.LineTo(new Point(x, lastY), true, false);
                    context.LineTo(new Point(x, y), true, false);
                }

                lastY = y;
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
        dc.DrawText(SmallText($"{series.Row.SignalName} · {series.Row.ChannelText}", pen.Brush), new Point(4, top + 1));
    }

    private double TimestampToX(long timestampNanoseconds, long minTime, long timeRange)
    {
        var usableWidth = Math.Max(1, ActualWidth - PlotEdgeInset * 2);
        return PlotEdgeInset + (timestampNanoseconds - minTime) * usableWidth / timeRange;
    }

    private long XToTimestamp(double x)
    {
        var range = Math.Max(1, _lastMaxTime - _lastMinTime);
        var usableWidth = Math.Max(1, ActualWidth - PlotEdgeInset * 2);
        return _lastMinTime + (long)(Math.Clamp((x - PlotEdgeInset) / usableWidth, 0, 1) * range);
    }

    /// <summary>Trace bookmarks as thin marker lines with a small flag glyph, so a signal that
    /// correlates with an already-flagged event is visible without switching back to Trace.</summary>
    private void DrawMarkers(DrawingContext dc, long minTime, long maxTime, long timeRange)
    {
        var markers = MarkerTimestamps;
        if (markers is null || markers.Count == 0)
        {
            return;
        }

        foreach (var timestamp in markers)
        {
            if (timestamp < minTime || timestamp > maxTime)
            {
                continue;
            }

            var x = TimestampToX(timestamp, minTime, timeRange);
            dc.DrawLine(_markerPen, new Point(x, 0), new Point(x, ActualHeight));
            dc.DrawText(SmallText("⚑", _markerPen.Brush), new Point(x + 2, 0));
        }
    }

    private void DrawTraceCursor(DrawingContext dc, long minTime, long timeRange)
    {
        var cursor = CursorTimestampNanoseconds;
        if (cursor < _lastMinTime || cursor > _lastMaxTime)
        {
            return;
        }

        var x = TimestampToX(cursor, minTime, timeRange);
        dc.DrawLine(_traceCursorPen, new Point(x, 0), new Point(x, ActualHeight));
        var label = SmallText($"◄ trace {cursor / 1_000_000_000d:F4} s");
        var labelX = x + 4 + label.Width > ActualWidth ? x - label.Width - 4 : x + 4;
        dc.DrawText(label, new Point(labelX, ActualHeight * 0.5));
    }

    private void DrawMeasurementCursors(
        DrawingContext dc,
        (PlotSeriesViewModel Row, SignalSample[] Points)[] series,
        long minTime,
        long timeRange)
    {
        var hasX1 = _measure1Ns >= _lastMinTime && _measure1Ns <= _lastMaxTime;
        var hasX2 = _measure2Ns >= _lastMinTime && _measure2Ns <= _lastMaxTime;
        if (hasX1)
        {
            var x = TimestampToX(_measure1Ns, minTime, timeRange);
            dc.DrawLine(_measure1Pen, new Point(x, 0), new Point(x, ActualHeight));
            dc.DrawText(SmallText("X1", _measure1Pen.Brush), new Point(x + 3, 2));
        }

        if (hasX2)
        {
            var x = TimestampToX(_measure2Ns, minTime, timeRange);
            dc.DrawLine(_measure2Pen, new Point(x, 0), new Point(x, ActualHeight));
            dc.DrawText(SmallText("X2", _measure2Pen.Brush), new Point(x + 3, 2));
        }

        if (!hasX1 && !hasX2)
        {
            return;
        }

        var lines = new List<string>();
        if (hasX1 && hasX2)
        {
            var deltaMs = Math.Abs((double)_measure2Ns - _measure1Ns) / 1_000_000d;
            lines.Add($"X1 {_measure1Ns / 1e9:F6}s  →  X2 {_measure2Ns / 1e9:F6}s  · Δt {deltaMs:F3} ms" +
                (deltaMs > 0.0001 ? $" · {1000d / deltaMs:F2} Hz" : ""));
        }
        else if (hasX1)
            lines.Add($"X1 {_measure1Ns / 1e9:F6}s  · actual CAN observation shown per signal");
        else if (hasX2)
            lines.Add($"X2 {_measure2Ns / 1e9:F6}s  · actual CAN observation shown per signal");

        var duplicateNames = series
            .GroupBy(item => item.Row.SignalName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (row, _) in series)
        {
            var label = duplicateNames.Contains(row.SignalName)
                ? $"{row.DatabaseName}/{row.ChannelText}/{row.MessageName}.{row.SignalName} (0x{row.IdentifierHex})"
                : row.SignalName;
            // Measurement readouts must use the original samples, NOT the
            // per-pixel decimated points used for drawing. "Nearest" can also
            // read a FUTURE CAN value; sample-and-hold requires the last value
            // at or before the timestamp. SignalTimeline.SampleAt is O(log N).
            var sample1 = hasX1 ? row.Timeline.SampleAt(_measure1Ns) : null;
            var sample2 = hasX2 ? row.Timeline.SampleAt(_measure2Ns) : null;
            var v1 = sample1?.Value;
            var v2 = sample2?.Value;
            var unit = string.IsNullOrWhiteSpace(row.Unit) ? string.Empty : $" {row.Unit}";
            string Fs(double value) => row.IsFullScale(value) ? " ○RAW" : string.Empty;
            // Statistics between the two cursors (min / max / time-weighted average over the window).
            var stats = hasX1 && hasX2 ? SignalRangeStatistics.Compute(row.Samples, _measure1Ns, _measure2Ns) : null;
            var statsText = stats is { } s
                ? $"  [min {s.Minimum:G5} · max {s.Maximum:G5} · avg {s.TimeWeightedMean:G5} · n={s.SampleCount:N0}]"
                : string.Empty;
            string Frame(SignalSample? sample, long cursorNs) => sample is not { } observed
                ? "no source frame"
                : (observed.FrameIndex >= 0
                    ? $"#{observed.FrameIndex + 1:N0}" : "original frame unknown") +
                  $" t={observed.TimestampNanoseconds / 1e9:F6}s" +
                  (observed.TimestampNanoseconds == cursorNs ? "" : " (held)");
            lines.Add((v1, v2) switch
            {
                ({ } a, { } b) =>
                    $"{label}: {a:G5}{Fs(a)} → {b:G5}{Fs(b)}{unit}  Δ {b - a:+0.#####;-0.#####;0}{unit}" +
                    $"  · frame {Frame(sample1, _measure1Ns)} → {Frame(sample2, _measure2Ns)}{statsText}",
                ({ } a, null) =>
                    $"{label} X1: {a:G5}{Fs(a)}{unit} · frame {Frame(sample1, _measure1Ns)}",
                (null, { } b) =>
                    $"{label} X2: {b:G5}{Fs(b)}{unit} · frame {Frame(sample2, _measure2Ns)}",
                _ => $"{label}: not yet observed at selected cursor"
            });
        }

        // A large set of plotted signals must not send measurement labels
        // past the top edge or cover the entire plot. Keep the data visible and
        // disclose the number of readouts omitted from this compact overlay.
        var maxLines = Math.Max(2, (int)((ActualHeight - 26) / 16));
        if (lines.Count > maxLines)
        {
            var omitted = lines.Count - maxLines + 1;
            lines.RemoveRange(maxLines - 1, lines.Count - maxLines + 1);
            lines.Add($"… {omitted} more readouts (inspect individual Quick Plots)");
        }

        var y = Math.Max(2, ActualHeight - lines.Count * 16 - 22);
        foreach (var line in lines)
        {
            var formatted = SmallText(line);
            formatted.MaxTextWidth = Math.Max(100, ActualWidth - 55);
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(formatted, new Point(46, y));
            y += 16;
        }
    }

    /// <summary>Mouse inspection tracks the nearest visible source-backed
    /// series at the hovered time. Vertical hit testing uses the exact Y-scale
    /// used to draw that curve, even with mixed physical units or digital
    /// lanes; the displayed value always comes from the original timeline.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_hasDataExtent || _hoverLanes.Count == 0 || e.LeftButton == MouseButtonState.Pressed)
            return;
        var point = e.GetPosition(this);
        if (point.X < PlotEdgeInset || point.X > ActualWidth - PlotEdgeInset ||
            point.Y < 0 || point.Y >= ActualHeight - TimeAxisLabelHeight)
        {
            if (_hoverEvidence is not null)
            {
                _hoverPoint = null;
                _hoverEvidence = null;
                SetHoverFallbackToolTip(null);
                InvalidateVisual();
            }
            return;
        }
        if (_hoverPoint is { } previous && Math.Abs(previous.X - point.X) < 2 &&
            Math.Abs(previous.Y - point.Y) < 5) return;

        var timestamp = XToTimestamp(point.X);
        HoverLane? closest = null;
        var distance = double.PositiveInfinity;
        foreach (var lane in _hoverLanes)
        {
            var source = lane.Row.Timeline.SampleAt(timestamp);
            if (source is not { } observed ||
                observed.TimestampNanoseconds < _lastMinTime)
                continue;
            var y = lane.Top + lane.Height -
                ((observed.Value - lane.Min) * lane.Height / lane.Range);
            var offset = Math.Abs(y - point.Y);
            if (offset >= distance) continue;
            closest = lane;
            distance = offset;
        }
        // If the pointer is away from every curve, use the explicitly
        // keyboard-active series (or first plotted curve) without making a
        // misleading geometric "you hovered this line" claim.
        var selected = distance <= 26 ? closest?.Row :
            _activeRow is not null && _hoverLanes.Any(l => ReferenceEquals(l.Row, _activeRow))
                ? _activeRow : _hoverLanes[0].Row;
        if (selected is null) return;
        var tolerance = Math.Max(1L, (long)Math.Min(30_000_000_000d,
            (double)(_lastMaxTime - _lastMinTime) * 10 / Math.Max(1d, ActualWidth)));
        _hoverRow = selected;
        _hoverEvidence = PlotSampleInspector.Inspect(selected.Samples, timestamp, tolerance,
            signal: selected.Timeline.Signal);
        _hoverPoint = point;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPoint = null;
        _hoverEvidence = null;
        _hoverRow = null;
        SetHoverFallbackToolTip(null);
        InvalidateVisual();
    }

    /// <summary>Small outlined dots mark real before/after transitions
    /// retained from original CAN frame evidence. They are deliberately
    /// THINNED and NOT an exhaustive map of every signal transition.</summary>
    private void DrawRecordedChangeDots(DrawingContext dc, long minTime, long timeRange)
    {
        foreach (var (row, points) in _cachedChangeDots)
        {
            var lane = _hoverLanes.FirstOrDefault(item => ReferenceEquals(item.Row, row));
            if (lane is null) continue;
            var index = Array.FindIndex(_lastSeries,
                item => ReferenceEquals(item.Row, row));
            if (index < 0) continue;
            var pen = _seriesPens[index % _seriesPens.Length];
            foreach (var point in points)
            {
                if (point.TimestampNanoseconds < minTime ||
                    point.TimestampNanoseconds > _lastMaxTime) continue;
                var x = TimestampToX(point.TimestampNanoseconds, minTime, timeRange);
                var y = lane.Top + lane.Height -
                    (point.Value - lane.Min) * lane.Height / lane.Range;
                if (!double.IsFinite(y) || y < 1 ||
                    y > ActualHeight - TimeAxisLabelHeight - 1) continue;
                dc.DrawEllipse(_labelBackgroundBrush, pen, new Point(x, y), 3, 3);
            }
        }
    }

    private void DrawHoverEvidence(DrawingContext dc, long minTime, long timeRange)
    {
        if (_hoverPoint is not { } pointer ||
            _hoverEvidence is not { } readout || _hoverRow is not { } row ||
            ActualWidth < 160 || ActualHeight < 100)
            return;

        var x = TimestampToX(readout.CursorNanoseconds, minTime, timeRange);
        dc.DrawLine(_hoverPen, new Point(x, 0),
            new Point(x, ActualHeight - TimeAxisLabelHeight));

        var unit = string.IsNullOrWhiteSpace(row.Unit) ? "" : " " + row.Unit;
        var canFormat = row.Timeline.SourceIsExtended ? "29-bit extended" : "11-bit standard";
        var lines = new List<string>
        {
            $"{row.MessageName}.{row.SignalName}",
            $"{row.ChannelText} · {canFormat} · observed ID 0x{row.IdentifierHex}",
            row.RecordingText
        };
        if (readout.LastObserved is not { } last)
        {
            lines.Add($"t={readout.CursorTimeText} · NO recorded value yet");
        }
        else
        {
            var ageNs = readout.ObservationAgeNanoseconds ?? 0;
            var age = ageNs == 0 ? "actual frame time" :
                ageNs < 1_000_000 ? $"held {ageNs / 1_000d:F0} µs" :
                ageNs < 1_000_000_000L ? $"held {ageNs / 1e6:F1} ms" :
                $"held {ageNs / 1e9:F2} s";
            var raw = readout.RawValue is { } rawValue
                ? $" · raw {rawValue.ToString("0", CultureInfo.InvariantCulture)}" : " · raw unavailable";
            var state = readout.DbcState is { } dbcState
                ? $" · DBC: {dbcState}" : string.Empty;
            var fullScale = row.IsFullScale(last.Value) ? " · ○RAW" : string.Empty;
            lines.Add($"At {readout.CursorTimeText}: {last.Value:G8}{unit}");
            lines.Add($"{raw.TrimStart(' ', '·')}{state}{fullScale}");
            lines.Add($"Source {readout.LastObservedTimeText} · {readout.FrameText} · {age}");
        }

        if (readout.ChangeBefore is { } previous &&
            readout.ChangeAfter is { } changed)
        {
            var delta = changed.Value - previous.Value;
            var orientation = readout.CandidateChangeIsAfterCursor
                ? "NEXT observed change" : "OBSERVED change";
            lines.Add($"{orientation} {previous.Value:G7} → {changed.Value:G7}{unit}  Δ {delta:+0.#######;-0.#######;0}");
            lines.Add($"Frames #{previous.FrameIndex + 1:N0} at {readout.ChangeBeforeTimeText} → #{changed.FrameIndex + 1:N0} at {readout.ChangeAfterTimeText} · Ctrl+click: Trace");
        }
        else
        {
            lines.Add("No changed source-frame pair near pointer; zoom in to inspect");
            lines.Add(readout.LastObserved is not null
                ? $"Last sample · Ctrl+click: Trace"
                : "No original CAN observation at or before this time");
        }

        // Small popover follows the pointer but stays inside the available
        // chart. Long OEM-defined names are shortened visually, never in the
        // source data. The time axis and X1/X2 cursor labels remain available.
        var maxWidth = Math.Min(450d, ActualWidth - 18);
        var format = lines.Select(line =>
        {
            var text = SmallText(line);
            text.MaxTextWidth = Math.Max(100, maxWidth - 16);
            text.Trimming = TextTrimming.CharacterEllipsis;
            return text;
        }).ToArray();
        var availableHeight = Math.Max(24, ActualHeight - TimeAxisLabelHeight - 8);
        var measuredHeight = format.Sum(item => item.Height + 2) + 10;
        var showFallback = measuredHeight > availableHeight;
        SetHoverFallbackToolTip(showFallback ? string.Join(Environment.NewLine, lines) : null);
        if (showFallback)
        {
            // Keep the visible card inside the chart and shrink its type before
            // clipping. The complete readout remains available in the fallback
            // tooltip when a compact plot cannot fit every evidence row.
            var fontSize = 12d;
            while (fontSize > 6 && format.Sum(item => item.Height + 2) + 10 > availableHeight)
            {
                fontSize -= 1;
                foreach (var item in format) item.SetFontSize(fontSize, 0, item.Text.Length);
            }
        }
        var boxHeight = Math.Min(availableHeight, format.Sum(item => item.Height + 2) + 10);
        var boxWidth = Math.Min(maxWidth, format.Max(item => item.Width) + 18);
        var boxX = pointer.X + 15;
        if (boxX + boxWidth > ActualWidth - 4) boxX = pointer.X - boxWidth - 15;
        boxX = Math.Clamp(boxX, 4, Math.Max(4, ActualWidth - boxWidth - 4));
        var plotBottom = ActualHeight - TimeAxisLabelHeight - 3;
        var boxY = Math.Clamp(pointer.Y + 15, 4,
            Math.Max(4, plotBottom - boxHeight));
        dc.DrawRoundedRectangle(_labelBackgroundBrush, _gridPen,
            new Rect(boxX, boxY, boxWidth, boxHeight), 5, 5);
        var textY = boxY + 5;
        foreach (var formatted in format)
        {
            if (textY + formatted.Height > boxY + boxHeight - 4) break;
            dc.DrawText(formatted, new Point(boxX + 8, textY));
            textY += formatted.Height + 2;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var position = e.GetPosition(this);

        if (e.ClickCount == 2)
        {
            _measure1Ns = -1;
            _measure2Ns = -1;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        foreach (var (bounds, row) in _legendHitBoxes)
        {
            if (bounds.Contains(position))
            {
                row.IsPlotted = false;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        if (!_hasDataExtent)
        {
            return;
        }

        var timestamp = XToTimestamp(position.X);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var selected = _hoverRow is not null && _selectedRows.Contains(_hoverRow)
                ? _hoverRow
                : (_activeRow is not null && _selectedRows.Contains(_activeRow)
                    ? _activeRow : _selectedRows.FirstOrDefault());
            var inspected = selected is null
                ? null
                : PlotSampleInspector.Inspect(selected.Samples, timestamp,
                    Math.Max(1, (long)Math.Min(30_000_000_000d,
                        (double)(_lastMaxTime - _lastMinTime) * 10 / Math.Max(1, ActualWidth))),
                    signal: selected.Timeline.Signal);
            // Prefer an exact original change frame when there is one close
            // to the click. Otherwise navigate to the last actually observed
            // frame, never to an invented future sample. If this plot has no
            // consumer for frame navigation, keep its prior seek behavior.
            var frame = inspected?.ChangeAfter?.FrameIndex ??
                inspected?.LastObserved?.FrameIndex ?? -1;
            if (frame >= 0 && OriginalFrameRequested is not null)
                OriginalFrameRequested.Invoke(this, frame);
            else
                SeekRequested?.Invoke(this, timestamp);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _measure2Ns = timestamp;
        }
        else
        {
            _measure1Ns = timestamp;
            CursorSelected?.Invoke(this, timestamp);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (!_hasDataExtent)
        {
            return;
        }

        _measure2Ns = XToTimestamp(e.GetPosition(this).X);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_hasDataExtent)
        {
            return;
        }

        var range = Math.Max(1, _lastMaxTime - _lastMinTime);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Pan by 15% of the window.
            var shift = (long)(range * 0.15) * (e.Delta > 0 ? -1 : 1);
            _viewStartNs = _lastMinTime + shift;
            _viewEndNs = _lastMaxTime + shift;
        }
        else
        {
            // Zoom around the pointer.
            var pivot = XToTimestamp(e.GetPosition(this).X);
            var factor = e.Delta > 0 ? 0.8 : 1.25;
            _viewStartNs = pivot - (long)((pivot - _lastMinTime) * factor);
            _viewEndNs = pivot + (long)((_lastMaxTime - pivot) * factor);
        }

        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private PlotSeriesViewModel? _activeRow;

    /// <summary>Keyboard: Shift+←/→ moves X1 one pixel (PageUp/PageDown = 10), Ctrl+←/→ moves X2;
    /// Shift/Ctrl+↑/↓ jump X1/X2 to the previous/next recorded sample of the active signal;
    /// Tab / Shift+Tab choose the active signal; L cycles its line style; Ctrl+0 resets the zoom.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.D0 && modifiers.HasFlag(ModifierKeys.Control))
        {
            ResetZoom();
            e.Handled = true;
            return;
        }

        if (!_hasDataExtent) return;
        var rows = _lastSeries.Select(item => item.Row).ToArray();
        if (rows.Length > 0 && (_activeRow is null || !rows.Contains(_activeRow))) _activeRow = rows[0];

        switch (e.Key)
        {
            case Key.Tab when rows.Length > 0 && modifiers is ModifierKeys.None or ModifierKeys.Shift:
                var current = Array.IndexOf(rows, _activeRow);
                var step = modifiers == ModifierKeys.Shift ? -1 : 1;
                _activeRow = rows[((current + step) % rows.Length + rows.Length) % rows.Length];
                break;
            case Key.L when _activeRow is not null && modifiers == ModifierKeys.None:
                _activeRow.LineStyle = PlotCursorMath.NextStyle(_activeRow.LineStyle);
                break;
            case Key.Left or Key.Right or Key.PageUp or Key.PageDown
                when modifiers is ModifierKeys.Shift or ModifierKeys.Control:
                var pixels = (e.Key is Key.Left or Key.PageUp ? -1 : 1) * (e.Key is Key.PageUp or Key.PageDown ? 10 : 1);
                MoveCursor(modifiers == ModifierKeys.Shift, PlotCursorMath.MoveByPixels(
                    modifiers == ModifierKeys.Shift ? _measure1Ns : _measure2Ns,
                    _lastMinTime, _lastMaxTime, Math.Max(1d, ActualWidth), pixels));
                break;
            case Key.Up or Key.Down when modifiers is ModifierKeys.Shift or ModifierKeys.Control && _activeRow is not null:
                var isX1 = modifiers == ModifierKeys.Shift;
                var from = isX1 ? _measure1Ns : _measure2Ns;
                if (from < _lastMinTime || from > _lastMaxTime) from = e.Key == Key.Down ? _lastMinTime - 1 : _lastMaxTime + 1;
                if (PlotCursorMath.NextSampleTime(_activeRow!.Samples, from, e.Key == Key.Down ? 1 : -1) is { } target)
                    MoveCursor(isX1, target);
                break;
            default:
                return;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private void MoveCursor(bool isX1, long timestamp)
    {
        if (isX1)
        {
            _measure1Ns = timestamp;
            CursorSelected?.Invoke(this, timestamp);
        }
        else
        {
            _measure2Ns = timestamp;
        }
    }

    private void DrawAxisLabels(
        DrawingContext dc,
        long minTime,
        long maxTime,
        double minValue,
        double maxValue,
        string yUnit,
        double analogHeight,
        bool showValueTicks = true)
    {
        var labelLeft = 4d;
        if (!string.IsNullOrEmpty(yUnit))
        {
            var unitText = SmallText(yUnit);
            var cx = unitText.Height;
            var cy = ActualHeight / 2;
            dc.PushTransform(new RotateTransform(-90, cx, cy));
            dc.DrawText(unitText, new Point(cx - unitText.Width / 2, cy - unitText.Height / 2));
            dc.Pop();
            labelLeft = unitText.Height + 6;
        }

        // A per-series scale has no single meaning on a shared axis — each series' own min/max
        // is shown in the legend instead (see DrawLegend), so the numeric ticks are skipped here
        // rather than printing a shared-axis number that would not apply to most of the curves.
        if (showValueTicks)
        {
            const int divisions = 5;
            for (var index = 0; index <= divisions; index++)
            {
                var fraction = index / (double)divisions;
                var value = maxValue - fraction * (maxValue - minValue);
                var text = SmallText(value.ToString("G5"));
                var y = fraction * analogHeight;
                var clampedY = Math.Clamp(y - text.Height / 2, 0, Math.Max(0, analogHeight - text.Height));
                dc.DrawText(text, new Point(labelLeft, clampedY));
            }
        }

        var startText = SmallText((minTime / 1_000_000_000d).ToString("F2") + " s");
        dc.DrawText(startText, new Point(labelLeft + 16, ActualHeight - startText.Height - 2));

        var endText = SmallText((maxTime / 1_000_000_000d).ToString("F2") + " s");
        dc.DrawText(endText, new Point(ActualWidth - endText.Width - 4, ActualHeight - endText.Height - 2));

        // Intermediate tick labels under the vertical time grid.
        for (var index = 1; index < TimeDivisions; index++)
        {
            var fraction = index / (double)TimeDivisions;
            var seconds = (minTime + fraction * (maxTime - minTime)) / 1_000_000_000d;
            var tick = SmallText(seconds.ToString("F2"));
            var x = ActualWidth * fraction - tick.Width / 2;
            dc.DrawText(tick, new Point(x, ActualHeight - tick.Height - 2));
        }
    }

    private void DrawLegend(
        DrawingContext dc, (PlotSeriesViewModel Row, SignalSample[] Points)[] series, bool showPerSeriesRange = false)
    {
        const double swatchSize = 10;
        const double rowHeight = 18;
        const double rangeRowHeight = 15;
        _legendHitBoxes.Clear();
        var y = 4d;
        for (var index = 0; index < series.Length; index++)
        {
            var row = series[index].Row;
            var points = series[index].Points;
            var brush = _seriesBrushes[index % _seriesBrushes.Length];
            var current = points.Length > 0 ? points[^1].Value.ToString("G5") : "-";
            var unitSuffix = string.IsNullOrWhiteSpace(row.Unit) ? string.Empty : $" {row.Unit}";
            // The hollow marker flags a possible source-defined all-ones code.
            // Never infer invalidity without that DBC's declared meaning.
            var fullScaleSuffix = points.Length > 0 && row.IsFullScale(points[^1].Value) ? " ○RAW" : string.Empty;
            var activeMark = ReferenceEquals(row, _activeRow) ? "► " : string.Empty;
            var formatted = SmallText($"{activeMark}{row.SignalName} = {current}{fullScaleSuffix}{unitSuffix}");

            // On a per-series Y scale, each curve's own min/max is the only place that range is
            // shown (the shared axis has no numeric ticks in this mode) — coloured to match its
            // swatch so it reads as "this curve's own scale" rather than a global figure.
            FormattedText? rangeText = null;
            if (showPerSeriesRange && points.Length > 0 && !row.IsDigitalSignal)
            {
                var rangeMin = points.Min(point => point.Value);
                var rangeMax = points.Max(point => point.Value);
                rangeText = SmallText($"{rangeMin:G4} … {rangeMax:G4}{unitSuffix}", brush);
            }

            var thisRowHeight = rowHeight + (rangeText is not null ? rangeRowHeight : 0);
            var contentWidth = Math.Max(formatted.Width, rangeText?.Width ?? 0);
            var x = ActualWidth - contentWidth - swatchSize - 12;
            if (x < 0)
            {
                continue;
            }

            var labelBounds = new Rect(
                Math.Max(0, x - 5),
                Math.Max(0, y - 2),
                contentWidth + swatchSize + 14,
                thisRowHeight);
            dc.DrawRoundedRectangle(_labelBackgroundBrush, null, labelBounds, 4, 4);
            dc.DrawRectangle(brush, null, new Rect(x, y + 3, swatchSize, swatchSize));
            dc.DrawText(formatted, new Point(x + swatchSize + 4, y));
            if (rangeText is not null)
            {
                dc.DrawText(rangeText, new Point(x + swatchSize + 4, y + rowHeight - 3));
            }

            _legendHitBoxes.Add((new Rect(x - 2, y, contentWidth + swatchSize + 8, thisRowHeight), row));
            y += thisRowHeight;
        }
    }

    private FormattedText SmallText(string text) => SmallText(text, _mutedTextBrush);

    private FormattedText SmallText(string text, Brush brush) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawCenteredText(DrawingContext dc, string text)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            14,
            _mutedTextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(20, ActualWidth - 40),
            MaxTextHeight = Math.Max(20, ActualHeight - 40)
        };

        dc.DrawText(
            formatted,
            new Point(
                Math.Max(12, (ActualWidth - formatted.Width) / 2),
                Math.Max(12, (ActualHeight - formatted.Height) / 2)));
    }


}
