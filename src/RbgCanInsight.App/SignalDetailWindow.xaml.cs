using System.Windows;
using System.Windows.Threading;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

public partial class SignalDetailWindow : Window
{
    public static readonly DependencyProperty DetailTimeWindowSecondsProperty =
        DependencyProperty.Register(
            nameof(DetailTimeWindowSeconds),
            typeof(double),
            typeof(SignalDetailWindow),
            new PropertyMetadata(30d, OnTimelinePropertyChanged));

    public static readonly DependencyProperty DetailWindowEndNanosecondsProperty =
        DependencyProperty.Register(
            nameof(DetailWindowEndNanoseconds),
            typeof(long),
            typeof(SignalDetailWindow),
            new PropertyMetadata(-1L, OnTimelinePropertyChanged));

    public static readonly DependencyProperty DetailTimelinePercentProperty =
        DependencyProperty.Register(
            nameof(DetailTimelinePercent),
            typeof(double),
            typeof(SignalDetailWindow),
            new PropertyMetadata(100d, OnTimelinePercentChanged));

    public static readonly DependencyProperty DetailNavigationTextProperty =
        DependencyProperty.Register(
            nameof(DetailNavigationText),
            typeof(string),
            typeof(SignalDetailWindow),
            new PropertyMetadata("No signal samples loaded."));

    public static readonly DependencyProperty DetailCumulativeRevealProperty =
        DependencyProperty.Register(
            nameof(DetailCumulativeReveal),
            typeof(bool),
            typeof(SignalDetailWindow),
            new PropertyMetadata(false, OnTimelinePropertyChanged));

    public static readonly DependencyProperty DetailsExpandedProperty =
        DependencyProperty.Register(
            nameof(DetailsExpanded),
            typeof(bool),
            typeof(SignalDetailWindow),
            new PropertyMetadata(true));

    public static readonly DependencyProperty DetailLinearInterpolationProperty =
        DependencyProperty.Register(
            nameof(DetailLinearInterpolation),
            typeof(bool),
            typeof(SignalDetailWindow),
            new PropertyMetadata(false));

    public static readonly DependencyProperty DetailPhysicalTextProperty =
        DependencyProperty.Register(nameof(DetailPhysicalText), typeof(string), typeof(SignalDetailWindow), new PropertyMetadata("—"));

    public static readonly DependencyProperty DetailRawTextProperty =
        DependencyProperty.Register(nameof(DetailRawText), typeof(string), typeof(SignalDetailWindow), new PropertyMetadata("—"));

    public static readonly DependencyProperty DetailStateTextProperty =
        DependencyProperty.Register(nameof(DetailStateText), typeof(string), typeof(SignalDetailWindow), new PropertyMetadata("—"));

    public static readonly DependencyProperty DetailSourceAgeTextProperty =
        DependencyProperty.Register(nameof(DetailSourceAgeText), typeof(string), typeof(SignalDetailWindow), new PropertyMetadata("Not observed"));

    public static readonly DependencyProperty DetailPlaybackSpeedProperty =
        DependencyProperty.Register(
            nameof(DetailPlaybackSpeed),
            typeof(double),
            typeof(SignalDetailWindow),
            new PropertyMetadata(1d, OnDetailPlaybackSpeedChanged));

    /// <summary>Same shared speed choices as Trace, Signals &amp; Plot and Byte Matrix.</summary>
    public static readonly double[] PlaybackSpeedChoices = [0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100];

    private readonly PlotSeriesViewModel _series;
    private readonly DispatcherTimer _playTimer;
    private readonly System.Diagnostics.Stopwatch _playbackClock = new();
    private bool _updatingTimeline;
    private bool _updatingSpeedFromStore;
    private bool _applyingCursorFromBroker;

    /// <summary>Raised with the source frame index of the sample at the current cursor position
    /// so the owner (MainWindow) can switch to Trace and select the exact frame.</summary>
    public event EventHandler<int>? JumpToTraceRequested;

    public SignalDetailWindow(PlotSeriesViewModel series, long initialTimestampNanoseconds = -1)
    {
        _series = series;
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _playTimer.Tick += (_, _) => AdvancePlayback();
        DetailsExpanded = AppSettingsStore.LoadQuickPlotDetailsExpanded();
        SetSpeedFromStore();
        PlaybackSpeedStore.Changed += OnSharedPlaybackSpeedChanged;
        InitializeComponent();
        TitleBarTheme.Hook(this);
        if (WindowPlacementStore.ResolveNamedSize("quickplot") is { } saved)
        {
            Width = Math.Max(MinWidth, saved.Width);
            Height = Math.Max(MinHeight, saved.Height);
            WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
        }

        DataContext = series;
        Title = $"Signal detail - {series.MessageName}.{series.SignalName} · {series.DatabaseName} · {series.RecordingText} · {series.ChannelText} · 0x{series.IdentifierHex}";
        Plot.ItemsSource = new[] { series };
        DetailWindowEndNanoseconds = series.Samples.Count == 0 ? -1
            : initialTimestampNanoseconds >= 0
                ? Math.Clamp(initialTimestampNanoseconds, series.Samples[0].TimestampNanoseconds,
                    series.Samples[^1].TimestampNanoseconds)
                : series.Samples[^1].TimestampNanoseconds;
        UpdateTimelinePercentFromEnd();
        UpdateNavigationText();
        UpdateCursorValueText();

        // Keep zoom/pan synced across every open detail window: broadcast this plot's own
        // changes, and apply anyone else's broadcast unless it originated from this same plot.
        Plot.ZoomChanged += OnPlotZoomChanged;
        Plot.CursorSelected += OnPlotCursorSelected;
        PlotSyncBroker.ZoomChanged += OnBrokerZoomChanged;
        PlotSyncBroker.CursorChanged += OnBrokerCursorChanged;
        Closed += (_, _) =>
        {
            _playTimer.Stop();
            Plot.ZoomChanged -= OnPlotZoomChanged;
            Plot.CursorSelected -= OnPlotCursorSelected;
            PlotSyncBroker.ZoomChanged -= OnBrokerZoomChanged;
            PlotSyncBroker.CursorChanged -= OnBrokerCursorChanged;
            PlaybackSpeedStore.Changed -= OnSharedPlaybackSpeedChanged;
            AppSettingsStore.SaveQuickPlotDetailsExpanded(DetailsExpanded);
            WindowPlacementStore.SaveNamed(this, "quickplot");
        };
    }

    private void OnPlotZoomChanged(object? sender, EventArgs e)
    {
        var range = Plot.ViewRange;
        PlotSyncBroker.NotifyZoomChanged(range.Start, range.End, Plot);
    }

    private void OnBrokerZoomChanged(long startNs, long endNs, object? source)
    {
        if (!ReferenceEquals(source, Plot))
        {
            Plot.SetViewRange(startNs, endNs);
        }
    }

    private void OnPlotCursorSelected(object? sender, long timestampNanoseconds)
    {
        // X1 is a user-selected instant: keep this Quick Plot's slider and
        // readout on it, instead of leaving the timeline at a different
        // playback timestamp. Send one broker event, not one per binding.
        if (_playTimer.IsEnabled)
            SetPlaybackState(false);
        _applyingCursorFromBroker = true;
        try
        {
            Plot.CursorTimestampNanoseconds = timestampNanoseconds;
            if (_series.Samples.Count > 0 &&
                timestampNanoseconds >= FirstTimestamp &&
                timestampNanoseconds <= LastTimestamp)
                DetailWindowEndNanoseconds = timestampNanoseconds;
            UpdateCursorValueText();
        }
        finally
        {
            _applyingCursorFromBroker = false;
        }
        PlotSyncBroker.NotifyCursorChanged(timestampNanoseconds, Plot);
    }

    private void OnBrokerCursorChanged(long timestampNanoseconds, object? source)
    {
        if (ReferenceEquals(source, Plot))
            return;

        _applyingCursorFromBroker = true;
        try
        {
            // Keep playback and crosshair at the same absolute trace time.
            // Do not clamp other CAN signals to this signal's first/last
            // sample: that would make different channels appear simultaneous.
            Plot.CursorTimestampNanoseconds = timestampNanoseconds;
            if (_series.Samples.Count > 0 &&
                timestampNanoseconds >= FirstTimestamp &&
                timestampNanoseconds <= LastTimestamp)
            {
                DetailWindowEndNanoseconds = timestampNanoseconds;
            }
            UpdateCursorValueText();
        }
        finally
        {
            _applyingCursorFromBroker = false;
        }
    }

    private void SetSpeedFromStore()
    {
        _updatingSpeedFromStore = true;
        try
        {
            DetailPlaybackSpeed = PlaybackSpeedStore.Current;
        }
        finally
        {
            _updatingSpeedFromStore = false;
        }
    }

    private void OnSharedPlaybackSpeedChanged(object? sender, EventArgs e) => SetSpeedFromStore();

    private static void OnDetailPlaybackSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var window = (SignalDetailWindow)d;
        if (!window._updatingSpeedFromStore)
        {
            PlaybackSpeedStore.Current = (double)e.NewValue;
        }
    }

    public double DetailTimeWindowSeconds
    {
        get => (double)GetValue(DetailTimeWindowSecondsProperty);
        set => SetValue(DetailTimeWindowSecondsProperty, Math.Clamp(value, 0.05, 86_400));
    }

    public long DetailWindowEndNanoseconds
    {
        get => (long)GetValue(DetailWindowEndNanosecondsProperty);
        set => SetValue(DetailWindowEndNanosecondsProperty, value);
    }

    public double DetailTimelinePercent
    {
        get => (double)GetValue(DetailTimelinePercentProperty);
        set => SetValue(DetailTimelinePercentProperty, Math.Clamp(value, 0, 100));
    }

    public string DetailNavigationText
    {
        get => (string)GetValue(DetailNavigationTextProperty);
        set => SetValue(DetailNavigationTextProperty, value);
    }

    public bool DetailCumulativeReveal
    {
        get => (bool)GetValue(DetailCumulativeRevealProperty);
        set => SetValue(DetailCumulativeRevealProperty, value);
    }

    public bool DetailsExpanded
    {
        get => (bool)GetValue(DetailsExpandedProperty);
        set => SetValue(DetailsExpandedProperty, value);
    }

    public bool DetailLinearInterpolation
    {
        get => (bool)GetValue(DetailLinearInterpolationProperty);
        set => SetValue(DetailLinearInterpolationProperty, value);
    }

    public string DetailPhysicalText
    {
        get => (string)GetValue(DetailPhysicalTextProperty);
        private set => SetValue(DetailPhysicalTextProperty, value);
    }

    public string DetailRawText
    {
        get => (string)GetValue(DetailRawTextProperty);
        private set => SetValue(DetailRawTextProperty, value);
    }

    public string DetailStateText
    {
        get => (string)GetValue(DetailStateTextProperty);
        private set => SetValue(DetailStateTextProperty, value);
    }

    public string DetailSourceAgeText
    {
        get => (string)GetValue(DetailSourceAgeTextProperty);
        private set => SetValue(DetailSourceAgeTextProperty, value);
    }

    /// <summary>Backed by the app-wide <see cref="PlaybackSpeedStore"/>, shared with Trace,
    /// Signals &amp; Plot and Byte Matrix so replaying the same moment looks the same everywhere.</summary>
    public double DetailPlaybackSpeed
    {
        get => (double)GetValue(DetailPlaybackSpeedProperty);
        set => SetValue(DetailPlaybackSpeedProperty, Math.Clamp(value, 0.1, 100));
    }

    private static void OnTimelinePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var window = (SignalDetailWindow)d;
        window.UpdateTimelinePercentFromEnd();
        window.UpdateNavigationText();
        if (e.Property == DetailWindowEndNanosecondsProperty)
        {
            var timestamp = (long)e.NewValue;
            window.Plot.CursorTimestampNanoseconds = timestamp;
            if (window.IsLoaded && !window._applyingCursorFromBroker)
                PlotSyncBroker.NotifyCursorChanged(timestamp, window.Plot);
        }
        window.UpdateCursorValueText();
    }

    private static void OnTimelinePercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var window = (SignalDetailWindow)d;
        if (window._updatingTimeline || window._series.Samples.Count == 0)
        {
            return;
        }

        window.DetailWindowEndNanoseconds = window.PercentToTimestamp((double)e.NewValue);
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text } &&
            double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            DetailTimeWindowSeconds = seconds;
            DetailWindowEndNanoseconds = _series.Samples.Count > 0 ? _series.Samples[^1].TimestampNanoseconds : -1;
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_playTimer.IsEnabled)
        {
            SetPlaybackState(false);
            return;
        }

        if (_series.Samples.Count == 0)
        {
            return;
        }

        if (DetailWindowEndNanoseconds < 0 || DetailWindowEndNanoseconds >= LastTimestamp)
        {
            DetailWindowEndNanoseconds = WindowEndFromStart();
        }

        SetPlaybackState(true);
    }

    private void SetPlaybackState(bool playing)
    {
        if (playing)
        {
            _playbackClock.Restart();
            _playTimer.Start();
            PlayPauseButton.Content = "⏸ Pause";
            PlayPauseButton.ToolTip = "Pause replay";
        }
        else
        {
            _playTimer.Stop();
            _playbackClock.Reset();
            PlayPauseButton.Content = "▶ Play";
            PlayPauseButton.ToolTip = "Replay this signal window from the current time";
        }
    }

    private void OnHeadClick(object sender, RoutedEventArgs e) => DetailWindowEndNanoseconds = WindowEndFromStart();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => MoveWindow(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => MoveWindow(1);

    private void OnTailClick(object sender, RoutedEventArgs e) => DetailWindowEndNanoseconds = LastTimestamp;

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        if (_series.Samples.Count == 0)
        {
            return;
        }

        DetailTimeWindowSeconds = Math.Max(
            0.05,
            (_series.Samples[^1].TimestampNanoseconds - _series.Samples[0].TimestampNanoseconds) / 1_000_000_000d);
        DetailWindowEndNanoseconds = _series.Samples[^1].TimestampNanoseconds;
        Plot.ResetZoom();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private long FirstTimestamp => _series.Samples.Count == 0 ? -1 : _series.Samples[0].TimestampNanoseconds;

    private long LastTimestamp => _series.Samples.Count == 0 ? -1 : _series.Samples[^1].TimestampNanoseconds;

    private void AdvancePlayback()
    {
        if (_series.Samples.Count == 0)
        {
            SetPlaybackState(false);
            return;
        }

        var elapsed = _playbackClock.Elapsed;
        _playbackClock.Restart();
        var current = DetailWindowEndNanoseconds < 0 ? WindowEndFromStart() : DetailWindowEndNanoseconds;
        // Sparse signals retain their last observed value until real recorded time
        // reaches the next sample. No O(N) sample scan or artificial fast-forward.
        var next = OfflineReplayClock.Advance(current, LastTimestamp, elapsed, DetailPlaybackSpeed);

        if (next >= LastTimestamp)
        {
            DetailWindowEndNanoseconds = LastTimestamp;
            SetPlaybackState(false);
            return;
        }

        DetailWindowEndNanoseconds = next;
    }

    private void MoveWindow(int direction)
    {
        if (_series.Samples.Count == 0)
        {
            return;
        }

        var current = DetailWindowEndNanoseconds < 0 ? LastTimestamp : DetailWindowEndNanoseconds;
        var step = Math.Max(1_000_000L, (long)(DetailTimeWindowSeconds * 1_000_000_000d * 0.8));
        DetailWindowEndNanoseconds = Math.Clamp(current + direction * step, FirstTimestamp, LastTimestamp);
    }

    private long WindowEndFromStart()
    {
        if (_series.Samples.Count == 0)
        {
            return -1;
        }

        var window = (long)(DetailTimeWindowSeconds * 1_000_000_000d);
        if (LastTimestamp > FirstTimestamp && window >= LastTimestamp - FirstTimestamp)
        {
            window = Math.Max(1_000_000L, (LastTimestamp - FirstTimestamp) / 20);
            DetailTimeWindowSeconds = window / 1_000_000_000d;
        }

        return Math.Min(LastTimestamp, FirstTimestamp + window);
    }

    private long PercentToTimestamp(double percent)
    {
        if (_series.Samples.Count == 0 || LastTimestamp <= FirstTimestamp)
        {
            return -1;
        }

        return FirstTimestamp + (long)((LastTimestamp - FirstTimestamp) * percent / 100d);
    }

    private void UpdateTimelinePercentFromEnd()
    {
        if (_series.Samples.Count == 0 || LastTimestamp <= FirstTimestamp)
        {
            return;
        }

        _updatingTimeline = true;
        try
        {
            DetailTimelinePercent = Math.Clamp((DetailWindowEndNanoseconds - FirstTimestamp) * 100d / (LastTimestamp - FirstTimestamp), 0, 100);
        }
        finally
        {
            _updatingTimeline = false;
        }
    }

    private void UpdateNavigationText()
    {
        if (_series.Samples.Count == 0)
        {
            DetailNavigationText = "No signal samples loaded.";
            return;
        }

        var end = DetailWindowEndNanoseconds < 0 ? LastTimestamp : DetailWindowEndNanoseconds;
        var start = DetailCumulativeReveal
            ? FirstTimestamp
            : Math.Max(FirstTimestamp, end - (long)(DetailTimeWindowSeconds * 1_000_000_000d));
        DetailNavigationText = $"{start / 1_000_000_000d:F3}s .. {end / 1_000_000_000d:F3}s · click = X1 · Shift/right-click = X2 · wheel = zoom";
    }

    /// <summary>Refreshes the "at cursor" physical/raw/state readout in the details panel for the
    /// sample at or before the current window-end timestamp (CAN sample-and-hold semantics).</summary>
    private void UpdateCursorValueText()
    {
        if (_series.Samples.Count == 0)
        {
            DetailPhysicalText = "—";
            DetailRawText = "—";
            DetailStateText = "—";
            DetailSourceAgeText = "No source frame";
            return;
        }

        var at = Plot.CursorTimestampNanoseconds >= 0
            ? Plot.CursorTimestampNanoseconds
            : DetailWindowEndNanoseconds < 0 ? LastTimestamp : DetailWindowEndNanoseconds;
        var (physical, raw, state) = _series.ValueAt(at);
        DetailPhysicalText = physical;
        DetailRawText = raw;
        DetailStateText = state;
        DetailSourceAgeText = _series.EvidenceAt(at).ProvenanceText;
    }

    private void OnOpenInTraceClick(object sender, RoutedEventArgs e)
    {
        if (_series.Samples.Count == 0)
        {
            return;
        }

        var at = Plot.CursorTimestampNanoseconds >= 0
            ? Plot.CursorTimestampNanoseconds
            : DetailWindowEndNanoseconds < 0 ? LastTimestamp : DetailWindowEndNanoseconds;
        var sample = _series.Timeline.SampleAt(at);
        if (sample is { } found)
        {
            JumpToTraceRequested?.Invoke(this, found.FrameIndex);
        }
    }
}
