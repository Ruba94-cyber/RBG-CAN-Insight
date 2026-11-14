using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.Correlation;

namespace RbgCanInsight.App;

public partial class MainWindow : Window
{
    private sealed record GridColumnLayout(string Header, double Width, int DisplayIndex, bool IsVisible);
    private sealed record UiLayoutPackage(int Version, int SelectedNavIndex, int ThemeIndex, Dictionary<string, GridColumnLayout[]> Grids);

    private readonly MainViewModel _viewModel;
    private readonly DataGrid[] _layoutGrids;
    private readonly HashSet<(string Grid, string Header)> _proportionalColumns;
    private Point _navDragStart;
    private RadioButton? _draggedNavButton;
    private bool _applyingCursorFromBroker;
    private bool _exportingCorrelationEvidence;
    private readonly HashSet<SignalDetailWindow> _openQuickPlots = [];
    private readonly HashSet<Controls.SignalPlotControl> _investigationCharts = [];
    private OnboardingWindow? _openTour;

    private const double DefaultWidth = 1480;
    private const double DefaultHeight = 900;

    public MainWindow()
    {
        _viewModel = new MainViewModel
        {
            ThemeIndex = AppSettingsStore.LoadThemeIndex(),
            SelectedNavIndex = Math.Clamp(AppSettingsStore.LoadSelectedNavIndex(), 0, MainViewModel.DataStudioNavIndex)
        };
        var plotPreferences = AppSettingsStore.LoadSignalsChartPreferences();
        _viewModel.Signals.LinearInterpolation = plotPreferences.Linear;
        _viewModel.Signals.YAxisModeIndex = plotPreferences.YAxisMode;
        _viewModel.Signals.ShowInlineLegend = plotPreferences.Legend;
        _viewModel.Signals.IsCumulativeReveal = plotPreferences.Cumulative;
        DataContext = _viewModel;
        InitializeComponent();
        TitleBarTheme.Hook(this);
        ApplyWorkspaceOrder();
        UtilityPanelExpander.IsExpanded = AppSettingsStore.LoadUtilityPanelExpanded();
        _traceOverviewPreferredHeight = AppSettingsStore.LoadTraceOverviewHeight();
        TraceOverviewRow.Height = new GridLength(_traceOverviewPreferredHeight);
        ByteMatrixOverviewRow.Height = new GridLength(AppSettingsStore.LoadByteMatrixOverviewHeight());
        ApplyStartupPlacement();
        _viewModel.CaptureViewScreenshot = CaptureContentHostImage;
        _layoutGrids = [TraceGrid, UdsTransactionsGrid, WatchGrid, SignalsGrid, MatrixGrid, ByteMatrixGrid, StatisticsGrid, MappingGrid];

        // Columns declared with a star width in XAML are meant to proportionally fill whatever
        // space is left, growing and shrinking with the window. Capture that set now, before any
        // saved layout is applied, so it never gets confused with columns a saved layout has since
        // turned into a fixed pixel width (which is what "IsStar" would report after the fact).
        _proportionalColumns = _layoutGrids
            .SelectMany(grid => grid.Columns.Where(column => column.Width.IsStar).Select(column => (grid.Name, ColumnHeader(column))))
            .ToHashSet();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Signals.PropertyChanged += OnSignalsCatalogChanged;
        Plot.ZoomChanged += OnMainPlotZoomChanged;
        Plot.CursorSelected += OnMainPlotCursorSelected;
        Plot.OriginalFrameRequested += OnPlotOriginalFrameRequested;
        PlotSyncBroker.ZoomChanged += OnBrokerPlotZoomChanged;
        PlotSyncBroker.CursorChanged += OnBrokerPlotCursorChanged;
        _viewModel.Trace.BringRowIntoViewRequested += (_, row) =>
        {
            TraceGrid.ScrollIntoView(row);
            _viewModel.PlotCursorNanoseconds = row.TimestampNanoseconds;
        };
        Plot.SeekRequested += (_, timestampNs) =>
        {
            _viewModel.SelectedNavIndex = 1;
            _viewModel.Trace.JumpToTimestamp(timestampNs);
        };
        TraceOverview.PercentRequested += (_, percent) => _viewModel.Trace.TimelinePercent = percent;
        Loaded += (_, _) =>
        {
            Common.GridLayoutHelpers.EnsureHeadersFit(this);
            LoadGridLayouts();
            LogStartupDiagnostics();
            if (!AppSettingsStore.LoadOnboardingShown())
            {
                OpenOnboardingTour();
            }
        };
        Closing += (_, e) =>
        {
            if (_viewModel.DbcEditor.HasStagedChanges &&
                MessageBox.Show(
                    "There are staged DBC edits not saved to a separate file. Exit and discard them?",
                    "Unsaved DBC working copies", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            SaveGridLayouts();
            SaveWorkspaceOrder();
            AppSettingsStore.SaveUtilityPanelExpanded(UtilityPanelExpander.IsExpanded);
            AppSettingsStore.SaveTraceOverviewHeight(_traceOverviewPreferredHeight);
            AppSettingsStore.SaveByteMatrixOverviewHeight(ByteMatrixOverviewRow.ActualHeight);
            AppSettingsStore.SaveSelectedNavIndex(_viewModel.SelectedNavIndex);
            AppSettingsStore.SaveSignalsChartPreferences(
                _viewModel.Signals.LinearInterpolation,
                _viewModel.Signals.YAxisModeIndex,
                _viewModel.Signals.ShowInlineLegend,
                _viewModel.Signals.IsCumulativeReveal);
            WindowPlacementStore.Save(this);
        };
        Closed += (_, _) =>
        {
            Plot.ZoomChanged -= OnMainPlotZoomChanged;
            Plot.CursorSelected -= OnMainPlotCursorSelected;
            Plot.OriginalFrameRequested -= OnPlotOriginalFrameRequested;
            PlotSyncBroker.ZoomChanged -= OnBrokerPlotZoomChanged;
            PlotSyncBroker.CursorChanged -= OnBrokerPlotCursorChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
            _viewModel.Signals.PropertyChanged -= OnSignalsCatalogChanged;
            _viewModel.ReleaseTraceStorage();
        };
    }

    /// <summary>Quick Plots retain decoded samples from their creation time.
    /// When a Trace or DBC reload reconstructs all timelines, close old
    /// windows rather than silently comparing signals from different imports
    /// under one shared absolute time cursor.</summary>
    private void OnSignalsCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SignalsViewModel.AllSeries))
            return;

        foreach (var detail in _openQuickPlots.ToArray())
            detail.Close();
    }

    private void OnMainPlotZoomChanged(object? sender, EventArgs e)
    {
        var (start, end) = Plot.ViewRange;
        PlotSyncBroker.NotifyZoomChanged(start, end, Plot);
    }

    private void OnBrokerPlotZoomChanged(long startNs, long endNs, object? source)
    {
        if (!ReferenceEquals(source, Plot))
            Plot.SetViewRange(startNs, endNs);
        foreach (var chart in _investigationCharts)
            if (!ReferenceEquals(source, chart))
                chart.SetViewRange(startNs, endNs);
    }

    // Investigation uses the SAME observed timelines and exact source cursor as the
    // main chart. Hooks are scoped to WPF Loaded/Unloaded so saved layouts can
    // create/destroy panes without leaking handlers or doubling input events.
    private void OnInvestigationPlotLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Controls.SignalPlotControl chart || !_investigationCharts.Add(chart)) return;
        chart.CursorSelected += OnMainPlotCursorSelected;
        chart.ZoomChanged += OnInvestigationPlotZoomChanged;
        chart.SeekRequested += OnInvestigationPlotSeekRequested;
        chart.OriginalFrameRequested += OnPlotOriginalFrameRequested;
        var (start, end) = Plot.ViewRange;
        chart.SetViewRange(start, end);
    }

    private void OnInvestigationPlotUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Controls.SignalPlotControl chart || !_investigationCharts.Remove(chart)) return;
        chart.CursorSelected -= OnMainPlotCursorSelected;
        chart.ZoomChanged -= OnInvestigationPlotZoomChanged;
        chart.SeekRequested -= OnInvestigationPlotSeekRequested;
        chart.OriginalFrameRequested -= OnPlotOriginalFrameRequested;
    }

    private void OnInvestigationPlotZoomChanged(object? sender, EventArgs e)
    {
        if (sender is Controls.SignalPlotControl chart)
        {
            var (start, end) = chart.ViewRange;
            PlotSyncBroker.NotifyZoomChanged(start, end, chart);
        }
    }

    private void OnInvestigationPlotSeekRequested(object? sender, long timestampNs)
    {
        _viewModel.SelectedNavIndex = 1;
        _viewModel.Trace.JumpToTimestamp(timestampNs);
    }

    private void OnPlotOriginalFrameRequested(object? sender, int originalFrameIndex)
    {
        if ((uint)originalFrameIndex >= (uint)_viewModel.Frames.Count)
            return;
        _viewModel.OpenTraceFrame(originalFrameIndex);
    }

    private void OnMainPlotCursorSelected(object? sender, long timestampNanoseconds) =>
        _viewModel.PlotCursorNanoseconds = timestampNanoseconds;

    private void OnBrokerPlotCursorChanged(long timestampNanoseconds, object? source)
    {
        if (ReferenceEquals(source, Plot))
            return;

        _applyingCursorFromBroker = true;
        try
        {
            _viewModel.PlotCursorNanoseconds = timestampNanoseconds;
        }
        finally
        {
            _applyingCursorFromBroker = false;
        }
    }

    private void OnWorkspaceNavMouseDown(object sender, MouseButtonEventArgs e)
    {
        _navDragStart = e.GetPosition(WorkspaceNavPanel);
        _draggedNavButton = FindAncestor<RadioButton>(e.OriginalSource as DependencyObject);
    }

    private void OnWorkspaceNavMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedNavButton is null) return;
        var current = e.GetPosition(WorkspaceNavPanel);
        if (Math.Abs(current.X - _navDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _navDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(_draggedNavButton, "RbgWorkspaceNav", DragDropEffects.Move);
    }

    private void OnWorkspaceNavDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _draggedNavButton is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnWorkspaceNavDrop(object sender, DragEventArgs e)
    {
        var dragged = _draggedNavButton;
        _draggedNavButton = null;
        if (dragged is null) return;
        var target = FindAncestor<RadioButton>(e.OriginalSource as DependencyObject);
        var remaining = WorkspaceNavPanel.Children.OfType<RadioButton>().Where(button => !ReferenceEquals(button, dragged)).ToList();
        var insertion = remaining.Count;
        if (target is not null && !ReferenceEquals(target, dragged))
        {
            insertion = remaining.IndexOf(target);
            if (e.GetPosition(target).Y > target.ActualHeight / 2) insertion++;
        }
        WorkspaceNavPanel.Children.Remove(dragged);
        WorkspaceNavPanel.Children.Insert(Math.Clamp(insertion, 0, WorkspaceNavPanel.Children.Count), dragged);
        SaveWorkspaceOrder();
        e.Handled = true;
    }

    private void ApplyWorkspaceOrder()
    {
        var order = AppSettingsStore.LoadWorkspaceOrder();
        if (order.Count == 0) return;
        var controls = WorkspaceNavPanel.Children.OfType<RadioButton>().ToDictionary(button => button.Name, StringComparer.Ordinal);
        var sorted = order.Where(controls.ContainsKey).Select(name => controls[name])
            .Concat(controls.Values.Where(button => !order.Contains(button.Name, StringComparer.Ordinal))).ToArray();
        WorkspaceNavPanel.Children.Clear();
        foreach (var button in sorted) WorkspaceNavPanel.Children.Add(button);
    }

    private void SaveWorkspaceOrder() => AppSettingsStore.SaveWorkspaceOrder(
        WorkspaceNavPanel.Children.OfType<RadioButton>().Select(button => button.Name).ToArray());

    private void OnUtilityPanelExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) AppSettingsStore.SaveUtilityPanelExpanded(UtilityPanelExpander.IsExpanded);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SmRemoteSession = 0x1000;

    /// <summary>Logs the WPF render tier, DPI scale and remote-session state once at startup, so
    /// a slowness report can be answered from the log instead of guessed at — tier 0 means the
    /// whole interface is being drawn on the CPU, and a remote/RDP session or a non-100% DPI
    /// scale materially changes what "slow" numbers in a bug report actually mean.</summary>
    private void LogStartupDiagnostics()
    {
        try
        {
            var tier = RenderCapability.Tier >> 16;
            var dpi = VisualTreeHelper.GetDpi(this);
            var isRemoteSession = GetSystemMetrics(SmRemoteSession) != 0;
            FileLog.Info(
                $"Startup: render tier={tier}, DPI scale={dpi.DpiScaleX:F2}x{dpi.DpiScaleY:F2}, " +
                $"remote session={isRemoteSession}, OS={Environment.OSVersion.VersionString}, " +
                $".NET={Environment.Version}, processors={Environment.ProcessorCount}");
        }
        catch (Exception exception)
        {
            FileLog.Error("LogStartupDiagnostics", exception);
        }
    }

    private void OnTraceOverviewResized(object sender, DragCompletedEventArgs e)
    {
        _traceOverviewPreferredHeight = TraceOverviewRow.ActualHeight;
        AppSettingsStore.SaveTraceOverviewHeight(_traceOverviewPreferredHeight);
        UpdateTraceToolsMaxHeight();
    }

    /// <summary>The density-strip height the user chose; the live row may be capped below it on short windows.</summary>
    private double _traceOverviewPreferredHeight = 48;

    private void OnTraceRootSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTraceToolsMaxHeight();

    /// <summary>Caps the Trace tool area so the frame table always keeps a usable minimum height;
    /// anything beyond the cap scrolls inside the tool area instead of hiding the table.</summary>
    private void UpdateTraceToolsMaxHeight()
    {
        if (TraceRootGrid is null || TraceTopScroll is null) return;
        // Keep the optional source explorer compact as the window shrinks. The
        // files remain scrollable instead of forcing the raw trace grid offscreen.
        if (TraceWorkspaceFilesScroll is not null)
            TraceWorkspaceFilesScroll.MaxHeight = Math.Clamp(TraceRootGrid.ActualHeight / 5, 72, 180);
        if (TraceRootGrid.ActualHeight > 0)
        {
            ApplyTraceSecondaryVisibility();
        }
        // On short windows the density strip shrinks to a thin band so the frame grid keeps its space.
        var compact = TraceRootGrid.ActualHeight < 640;
        TraceOverviewRow.MaxHeight = compact ? 36 : 520;
        var overview = compact ? 36 : Math.Max(TraceOverviewRow.MinHeight, TraceOverviewRow.ActualHeight);
        const double minTableHeight = 150, splitter = 6, gaps = 16, floor = 90;
        var available = TraceRootGrid.ActualHeight - overview - splitter - minTableHeight - gaps;
        TraceTopScroll.MaxHeight = Math.Max(floor, available);
    }

    private void OnByteMatrixOverviewResized(object sender, DragCompletedEventArgs e) =>
        AppSettingsStore.SaveByteMatrixOverviewHeight(ByteMatrixOverviewRow.ActualHeight);

    /// <summary>
    /// Places the window using the last saved position only if it still lands on a currently
    /// connected monitor; otherwise (first run, a monitor was unplugged, or the portable folder
    /// was moved to a different machine) it centers the window on the primary monitor's work
    /// area instead of trusting the OS/WPF default, which has no such validation.
    /// </summary>
    private void ApplyStartupPlacement()
    {
        var (bounds, maximized) = WindowPlacementStore.ResolveStartupPlacement(DefaultWidth, DefaultHeight);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Renders whichever analysis view is currently on screen (Trace, Signals &amp; Plot, Change
    /// Matrix, etc. all live inside <c>ContentHost</c>, one visible at a time) so the exported
    /// report can include a screenshot of exactly what the analyst was looking at.
    /// </summary>
    private byte[]? CaptureContentHostImage()
    {
        if (ContentHost.ActualWidth <= 0 || ContentHost.ActualHeight <= 0)
        {
            return null;
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(ContentHost.ActualWidth),
            (int)Math.Ceiling(ContentHost.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(ContentHost);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Remembers the height the user just dragged the Byte Matrix decoded-signal panel
    /// to, so reopening it (by selecting another row) keeps that size instead of resetting to the
    /// default 220px every time.</summary>
    private void OnByteMatrixDecodedPanelResized(object sender, DragCompletedEventArgs e)
    {
        if (Resources["ByteMatrixDecodedHeight"] is CollapsibleRowHeightConverter converter &&
            ByteMatrixDecodedRow.ActualHeight > 0)
        {
            converter.RememberedHeight = ByteMatrixDecodedRow.ActualHeight;
        }
    }

    /// <summary>Safety net for a window stuck off-screen or on a disconnected monitor.</summary>
    private void OnResetWindowPositionClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Normal;
        var bounds = WindowPlacementStore.CenteredOnPrimaryWorkArea(DefaultWidth, DefaultHeight);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ThemeIndex))
        {
            ThemeManager.Apply(_viewModel.ThemeIndex);
            AppSettingsStore.SaveThemeIndex(_viewModel.ThemeIndex);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PlotCursorNanoseconds))
        {
            if (!_applyingCursorFromBroker)
                PlotSyncBroker.NotifyCursorChanged(_viewModel.PlotCursorNanoseconds, Plot);
            return;
        }

        // When a source link selects an off-screen DBC definition,
        // virtualization must not leave the analyst staring at another row.
        // Defer until ItemsSource, sorting and navigation visibility settle;
        // do not steal focus from the user's search field.
        if (e.PropertyName == nameof(MainViewModel.SelectedDbcMessage) ||
            e.PropertyName == nameof(MainViewModel.SelectedDbcSignal))
        {
            var showMessage = e.PropertyName == nameof(MainViewModel.SelectedDbcMessage);
            var grid = showMessage ? DbcMessagesGrid : DbcSignalsGrid;
            object? selected = showMessage
                ? _viewModel.SelectedDbcMessage : _viewModel.SelectedDbcSignal;
            if (selected is not null)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        if (ReferenceEquals(grid.SelectedItem, selected) && grid.IsVisible)
                            grid.ScrollIntoView(selected);
                    }));
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.SelectedNavIndex))
        {
            return;
        }

        // Keep the nav rail in sync when a view switches programmatically (e.g. matrix → plot).
        var target = _viewModel.SelectedNavIndex switch
        {
            1 => NavTrace,
            2 => NavDbcManager,
            3 => NavSignals,
            4 => NavMatrix,
            5 => NavByteMatrix,
            6 => NavStatistics,
            7 => NavMapping,
            8 => NavUds,
            MainViewModel.InvestigationNavIndex => NavInvestigation,
            MainViewModel.DataStudioNavIndex => NavDataStudio,
            _ => NavDashboard
        };
        if (target.IsChecked != true)
        {
            target.IsChecked = true;
        }
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SelectedNavIndex = sender switch
        {
            _ when ReferenceEquals(sender, NavTrace) => 1,
            _ when ReferenceEquals(sender, NavDbcManager) => 2,
            _ when ReferenceEquals(sender, NavSignals) => 3,
            _ when ReferenceEquals(sender, NavMatrix) => 4,
            _ when ReferenceEquals(sender, NavByteMatrix) => 5,
            _ when ReferenceEquals(sender, NavStatistics) => 6,
            _ when ReferenceEquals(sender, NavMapping) => 7,
            _ when ReferenceEquals(sender, NavUds) => 8,
            _ when ReferenceEquals(sender, NavInvestigation) => MainViewModel.InvestigationNavIndex,
            _ when ReferenceEquals(sender, NavDataStudio) => MainViewModel.DataStudioNavIndex,
            _ => 0
        };
    }

    /// <summary>
    /// Ctrl+F focuses the search/filter box of whichever workspace is active. Handled at the
    /// window level (Ctrl-modified, so it never steals a plain keystroke from a text box) since
    /// each workspace names its own search box rather than sharing one control.
    /// </summary>
    private void OnGlobalSearchClick(object sender, RoutedEventArgs e) => OpenGlobalSearch();

    private void OnQuickTourClick(object sender, RoutedEventArgs e) => OpenOnboardingTour();

    /// <summary>One non-modal guide at a time; repeatedly pressing F1
    /// brings the existing tour forward instead of spawning duplicates.</summary>
    private void OpenOnboardingTour()
    {
        if (_openTour is { IsVisible: true })
        {
            _openTour.Activate();
            return;
        }
        var window = new OnboardingWindow(_viewModel) { Owner = this };
        _openTour = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openTour, window))
                _openTour = null;
        };
        window.Show();
    }

    private void OpenGlobalSearch() => new GlobalSearchWindow(_viewModel) { Owner = this }.ShowDialog();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenOnboardingTour();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenGlobalSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var searchBox = _viewModel.SelectedNavIndex switch
            {
                1 => TraceSearchBox,
                2 => DbcSearchBox,
                3 => SignalsSearchBox,
                4 => MatrixSearchBox,
                5 => ByteMatrixFilterBox,
                6 => StatisticsSearchBox,
                7 => MappingSearchBox,
                8 when _viewModel.Uds.SelectedViewIndex == 5 => UdsCorrelationSearchBox,
                8 => UdsSearchBox,
                _ => null
            };
            if (searchBox is null)
            {
                // Dashboard and other views with no local filter still
                // deserve the same find gesture as the workspaces.
                OpenGlobalSearch();
                e.Handled = true;
                return;
            }

            searchBox.Focus();
            searchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // The correlation lens has both a text query and a source-only
        // switch: Esc resets them together, never leaving a hidden filter
        // that makes a user think matching DID/CAN evidence is missing.
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None &&
            ReferenceEquals(Keyboard.FocusedElement, UdsCorrelationSearchBox))
        {
            _viewModel.Uds.ClearCorrelationFiltersCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Escape clears a focused workspace search, not the signal plot,
        // watch list, file selection or any unsaved DBC edits.
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None &&
            Keyboard.FocusedElement is TextBox focusedSearch &&
            (ReferenceEquals(focusedSearch, SignalsSearchBox) ||
             ReferenceEquals(focusedSearch, DbcSearchBox) ||
             ReferenceEquals(focusedSearch, MatrixSearchBox) ||
             ReferenceEquals(focusedSearch, ByteMatrixFilterBox) ||
             ReferenceEquals(focusedSearch, StatisticsSearchBox) ||
             ReferenceEquals(focusedSearch, MappingSearchBox) ||
             ReferenceEquals(focusedSearch, UdsSearchBox)))
        {
            focusedSearch.Clear();
            e.Handled = true;
            return;
        }

        // Ctrl+Enter opens the exact selected observed signal in its own
        // Quick Plot; never steal Enter from a text editor or ComboBox.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control &&
            _viewModel.SelectedNavIndex == 3 && IsSafeForPlainKeyShortcut())
        {
            if (_viewModel.Signals.SelectedSeries is { } selected)
                OpenSignalSeriesDetail(selected, _viewModel.Signals.PlotWindowEndNanoseconds);
            else
                _viewModel.StatusText = "Select a recorded signal to open Quick Plot.";
            e.Handled = true;
            return;
        }

        if (HandleReplayKey(e)) return;

        // F3 always finds the next match while the raw Trace workspace is active,
        // even when the keyboard focus is on the frame grid rather than in Search.
        if (e.Key == Key.F3 && _viewModel.SelectedNavIndex == 1 &&
            (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                _viewModel.Trace.FindPreviousCommand.Execute(null);
            else
                _viewModel.Trace.FindNextCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3 && _viewModel.SelectedNavIndex == 3 &&
            (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                _viewModel.Signals.PreviousChangeCommand.Execute(null);
            else
                _viewModel.Signals.NextChangeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // The same next/previous evidence gesture also works in UDS↔CAN.
        // F3 is never interpreted as a source claim or a fabricated sample;
        // it only selects one of the already recorded frame pairs.
        if (e.Key == Key.F3 && _viewModel.SelectedNavIndex == 8 &&
            _viewModel.Uds.SelectedViewIndex == 5 &&
            (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                _viewModel.Uds.PreviousCorrelationPairCommand.Execute(null);
            else
                _viewModel.Uds.NextCorrelationPairCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+G mirrors a CANalyzer-style Go To Time action. Do not steal
        // Ctrl+G from editors while a different workspace is selected.
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control &&
            _viewModel.SelectedNavIndex == 1)
        {
            TraceGoTimeBox.Focus();
            TraceGoTimeBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _viewModel.SelectedNavIndex == 3)
        {
            Plot.ResetZoom();
            e.Handled = true;
            return;
        }

        // "M" and Space are plain, unmodified keys — the same keys a user types into any search
        // box, or presses to activate a focused button/checkbox/radio button/combo box. Only
        // treat them as shortcuts when focus is on something that has no competing meaning for
        // that key (e.g. a DataGrid row), never while editing text or operating a control that
        // uses Space/letters itself.
        if (Keyboard.Modifiers != ModifierKeys.None || !IsSafeForPlainKeyShortcut())
        {
            return;
        }

        if (e.Key == Key.M && _viewModel.SelectedNavIndex == 1)
        {
            _viewModel.Trace.ToggleMarkerCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            var playback = _viewModel.SelectedNavIndex switch
            {
                1 => _viewModel.Trace.TogglePlaybackCommand,
                3 => _viewModel.Signals.TogglePlaybackCommand,
                5 => _viewModel.ByteMatrix.TogglePlaybackCommand,
                _ => null
            };
            if (playback is null)
            {
                return;
            }

            playback.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>True when keyboard focus is on an element with no meaning of its own for a plain
    /// letter or Space keystroke (so shortcuts like "M" or Space are safe to intercept there).</summary>
    private static bool IsSafeForPlainKeyShortcut() =>
        Keyboard.FocusedElement switch
        {
            System.Windows.Controls.Primitives.TextBoxBase => false,
            System.Windows.Controls.ComboBox => false,
            System.Windows.Controls.Primitives.ButtonBase => false,
            System.Windows.Controls.PasswordBox => false,
            _ => true
        };

    /// <summary>
    /// Unified trace search box: Enter applies the filter, F3/Shift+F3 navigate matches
    /// without hiding rows, Esc clears both.
    /// </summary>
    private void OnTraceGoTimeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _viewModel.Trace.GoToTimeCommand.Execute(TraceGoTimeBox.Text);
        e.Handled = true;
    }

    private void OnTraceInspectUdsClick(object sender, RoutedEventArgs e) =>
        _viewModel.InspectSelectedTraceUdsCommand.Execute(null);

    private void OnSignalOverlayClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SignalOverlayContext is { } context)
            new SignalOverlayWindow(_viewModel, context) { Owner = this }.Show();
    }

    private void OnEditDiagnosticTextClick(object sender, RoutedEventArgs e)
    {
        var row = _viewModel.CatalogExplorer.SelectedDefinition;
        if (row is not { Kind: "DID", Did: int did })
        {
            _viewModel.StatusText = "Select a DID definition in the catalog first.";
            return;
        }
        var parameter = _viewModel.Mapping.Catalogs.SelectMany(catalog => catalog.Parameters).FirstOrDefault(item =>
            item.Did == did &&
            string.Equals(item.SourceFile, row.SourceFile, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Ecu, row.Ecu, StringComparison.Ordinal));
        if (parameter is null)
        {
            _viewModel.StatusText = "The selected definition is no longer loaded.";
            return;
        }
        new DiagnosticEditWindow(new DiagnosticEditViewModel(parameter)) { Owner = this }.Show();
    }

    private void OnSpecVsTraceClick(object sender, RoutedEventArgs e) =>
        new SpecTraceWindow(_viewModel) { Owner = this }.Show();

    private void OnCatalogCheckClick(object sender, RoutedEventArgs e) =>
        new CatalogCheckWindow(() => _viewModel.Mapping.Catalogs) { Owner = this }.Show();

    /// <summary>A context action must apply to the frame/message/signal
    /// the analyst actually right-clicked. WPF DataGrid does not guarantee
    /// right-click selects that row: without this, "Filter by ID", plot,
    /// compare and confirm/reject can operate on an older unrelated row.</summary>
    private void OnContextGridPreviewRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid ||
            e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row ||
            row.Item is null || row.Item == System.Windows.Data.CollectionView.NewItemPlaceholder)
            return;

        if (!ReferenceEquals(grid.SelectedItem, row.Item))
            grid.SelectedItem = row.Item;

        // Let WPF open its normal context menu; the change is only which
        // recorded source row the existing commands will act upon.
    }

    private void OnUdsCorrelationSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        _viewModel.Uds.ClearCorrelationFiltersCommand.Execute(null);
        e.Handled = true;
    }

    private void OnUdsCorrelationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not { } selected)
            return;

        // Changing search filters and jumping from DBC/Trace can select a
        // virtualized row outside the currently realized viewport. Reveal
        // the chosen source AFTER WPF has applied the new ItemsSource.
        // Never force keyboard focus away from the active search box.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (ReferenceEquals(grid.SelectedItem, selected) && grid.IsVisible)
                    grid.ScrollIntoView(selected);
            }));
    }

    private void OnUdsCorrelationPointSelected(object? sender, int pairIndex) =>
        _viewModel.Uds.SelectedCorrelationPairIndex = pairIndex;

    private async void OnExportCorrelationEvidenceClick(object sender, RoutedEventArgs e)
    {
        if (_exportingCorrelationEvidence)
            return;

        if (_viewModel.Uds.SelectedCorrelation is not { HasSourcePairs: true } selected)
        {
            _viewModel.StatusText =
                "Select a correlation with original DID/CAN source-frame pairs before exporting.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export recorded UDS / CAN correlation evidence",
            FileName = $"UDS_CAN_DID_{selected.Candidate.Did.Did:X4}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV (source evidence)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _exportingCorrelationEvidence = true;
        try
        {
            var count = await Task.Run(() =>
            {
                using var writer = new StreamWriter(dialog.FileName, false,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return CorrelationEvidenceCsvExporter.Write(writer, selected);
            });
            _viewModel.StatusText =
                $"Exported {count:N0} original DID/CAN evidence pairs to {Path.GetFileName(dialog.FileName)}. " +
                "The statistical association remains unverified until source review.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.StatusText = $"Cannot export correlation evidence: {ex.Message}";
        }
        finally
        {
            _exportingCorrelationEvidence = false;
        }
    }

    private void OnUdsQuickViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string view } &&
            int.TryParse(view, out var index))
        {
            _viewModel.Uds.SelectedViewIndex = index;
        }
    }

    /// <summary>Offline replay keys on the Trace workspace: F7 step (Shift+F7 back), F8 animate, F9 run,
    /// Ctrl+F9 run to the next match of the search box condition (Ctrl+Shift+F9 previous), Esc break.</summary>
    private bool HandleReplayKey(KeyEventArgs e)
    {
        if (_viewModel.SelectedNavIndex != 1) return false;
        var trace = _viewModel.Trace;
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.F7 when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                (modifiers == ModifierKeys.Shift ? trace.StepBackCommand : trace.StepForwardCommand).Execute(null);
                break;
            case Key.F8 when modifiers == ModifierKeys.None:
                trace.AnimateCommand.Execute(null);
                break;
            case Key.F9 when modifiers == ModifierKeys.None:
                trace.RunCommand.Execute(null);
                break;
            case Key.F9 when modifiers == ModifierKeys.Control:
                trace.RunToNextMatchCommand.Execute(null);
                break;
            case Key.F9 when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                trace.RunToPreviousMatchCommand.Execute(null);
                break;
            case Key.Escape when trace.IsReplayActive:
                trace.PauseCommand.Execute(null);
                break;
            default:
                return false;
        }

        e.Handled = true;
        return true;
    }

    private void OnTraceSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.Trace.ApplyFilterCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F3 when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                _viewModel.Trace.FindPreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F3:
                _viewModel.Trace.FindNextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _viewModel.Trace.ClearFilterCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnMatrixRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Matrix.SelectedRow is not null)
        {
            _viewModel.Matrix.PlotSelectedCommand.Execute(null);
        }
    }

    private void OnPlotMatrixRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MatrixRowViewModel row })
        {
            _viewModel.Matrix.SelectedRow = row;
            _viewModel.Matrix.PlotSelectedCommand.Execute(null);
        }
    }

    private void OnPlotCompareSignalClick(object sender, RoutedEventArgs e) => OpenCompareSignalChart(_viewModel.SelectedCompareSignalRow);

    private void OnMergeDbcsClick(object sender, RoutedEventArgs e) =>
        new DbcMergeWindow { Owner = this }.Show();

    private void OnTraceHealthClick(object sender, RoutedEventArgs e) =>
        new TraceHealthWindow(_viewModel) { Owner = this }.Show();

    private void OnTraceCompareFramesClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Trace.BuildFrameDifference() is { } result)
            new FrameDifferenceWindow(result) { Owner = this }.Show();
    }

    private void OnQueryBuilderClick(object sender, RoutedEventArgs e) =>
        OpenQueryBuilder().Show();

    public QueryBuilderWindow OpenQueryBuilder(Common.SavedQueryStore? store = null)
    {
        var builder = new ViewModels.QueryBuilderViewModel(
            store ?? new Common.SavedQueryStore(), _viewModel.Trace.ApplyQuery, _viewModel.Trace.AppliedQuery,
            _viewModel.Trace.SetRowColourRules);
        return new QueryBuilderWindow(builder) { Owner = this };
    }

    private void OnLoadPresetClick(object sender, RoutedEventArgs e) =>
        new PresetLibraryWindow(_viewModel) { Owner = this }.Show();

    private void OnDbcCycleIncidentDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The selected source frame is the *end* of the gap. Keep the
        // Trace time cursor aligned when opening this evidence.
        _viewModel.CycleTiming.OpenIncidentCommand.Execute(null);
        e.Handled = _viewModel.CycleTiming.SelectedIncident is not null;
    }

    private void OnCompareSignalRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: CompareSignalRow row })
        {
            OpenCompareSignalChart(row);
        }
    }

    private void OpenCompareSignalChart(CompareSignalRow? row)
    {
        if (row is null)
        {
            MessageBox.Show("Select a signal row first.", "Compare signal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.BaseReport is null && row.CompareReport is null)
        {
            MessageBox.Show("Neither trace has decoded samples for this signal.", "Compare signal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new CompareDiffWindow(row) { Owner = this }.Show();
    }

    /// <summary>Plots every decoded signal of the clicked row's message in one click, without
    /// requiring the row detail sub-panel to be opened first.</summary>
    private void OnPlotTraceRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TraceRowViewModel row })
        {
            return;
        }

        _viewModel.Trace.SelectedRow = row;
        _viewModel.PlotSelectedTraceMessage(navigateToPlot: false);
    }

    /// <summary>Trace row ↗: opens an independent Quick Plot window; with several
    /// decoded signals the user picks which one from a small menu.</summary>
    private void OnQuickPlotTraceRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TraceRowViewModel row } anchor)
            return;

        _viewModel.Trace.SelectedRow = row;
        var signals = _viewModel.Trace.SelectedRowSignals;
        if (signals.Count == 0)
        {
            _viewModel.StatusText = "This frame has no decoded signal. Load a matching DBC to plot it.";
            return;
        }

        void Open(SignalDetailRow s)
        {
            var series = _viewModel.Signals.FindObservedSignal(s.Message, s.Signal,
                channel: s.SourceChannel >= 0 ? s.SourceChannel : null,
                databaseName: string.IsNullOrWhiteSpace(s.DatabaseName) ? null : s.DatabaseName,
                identifier: s.DefinitionIdentifier, isExtended: s.SourceIsExtended,
                sourceIdentifier: s.FrameIdentifier);
            if (series is null)
            {
                _viewModel.StatusText = $"No recorded samples for {s.Message}.{s.Signal}.";
                return;
            }
            OpenSignalSeriesDetail(series, row.TimestampNanoseconds);
        }

        if (signals.Count == 1)
        {
            Open(signals[0]);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = anchor, IsOpen = false };
        foreach (var s in signals)
        {
            var item = new MenuItem { Header = $"{s.Message} · {s.Signal}" };
            var captured = s;
            item.Click += (_, _) => Open(captured);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Plots every decoded signal of the clicked byte matrix ID in one click.</summary>
    private void OnPlotByteMatrixRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ByteMatrixRowViewModel row })
        {
            return;
        }

        _viewModel.ByteMatrix.SelectedRow = row;
        _viewModel.PlotSelectedByteMatrixRowCommand.Execute(null);
    }

    private void OnToggleByteMatrixDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ByteMatrixRowViewModel row })
        {
            return;
        }

        _viewModel.ByteMatrix.SelectedRow = ReferenceEquals(_viewModel.ByteMatrix.SelectedRow, row)
            ? null
            : row;
        e.Handled = true;
    }

    private void OnOfflineFindingDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestorSafe<DataGridRow>(e.OriginalSource)?.Item is OfflineFindingRowViewModel { FirstFrameIndex: >= 0 } row)
        {
            _viewModel.Uds.OpenEvidence(row.FirstFrameIndex);
            e.Handled = true;
        }
    }

    private void OnDiagnosticSequenceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestorSafe<DataGridRow>(e.OriginalSource)?.Item is DiagnosticSequenceRowViewModel { FirstFrameIndex: >= 0 } row)
        {
            _viewModel.Uds.OpenEvidence(row.FirstFrameIndex);
            e.Handled = true;
        }
    }

    private void OnEcuSnapshotDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestorSafe<DataGridRow>(e.OriginalSource)?.Item is RecordedEcuValueRowViewModel { FrameIndex: >= 0 } row)
        {
            _viewModel.Uds.OpenEvidence(row.FrameIndex);
            e.Handled = true;
        }
    }

    /// <summary>Shared by every <see cref="Controls.DecodedSignalGrid"/> instance (Trace and Byte
    /// Matrix) for both its dedicated-window button and row double-click.</summary>
    private void OnSignalDetailRequested(object? sender, SignalDetailRow signal) => OpenTraceSignalDetail(signal);

    /// <summary>Double-click on a signal row mirrors the Details arrow: it opens/closes the
    /// row's inline mini-plot (plotting into the main chart stays on the checkbox / context
    /// menu, which are explicit).</summary>
    private void OnSignalSeriesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var origin = e.OriginalSource as DependencyObject;
        if (FindAncestorSafe<ButtonBase>(origin) is not null ||
            FindAncestorSafe<System.Windows.Controls.Primitives.DataGridDetailsPresenter>(origin) is not null)
        {
            return;
        }

        if (FindAncestorSafe<DataGridRow>(origin)?.Item is PlotSeriesViewModel series)
        {
            series.IsDetailOpen = !series.IsDetailOpen;
            e.Handled = true;
        }
    }

    /// <summary>The Details arrow in the signal list — the explicit way to open/close a row's
    /// inline mini-plot.</summary>
    private void OnToggleSignalDetailClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlotSeriesViewModel series })
        {
            series.IsDetailOpen = !series.IsDetailOpen;
        }
    }

    /// <summary>Every time-window control (timeline scrub, Fit/10s/30s/60s, ＋/－) drives the
    /// MAIN plot: a previous manual wheel-zoom would otherwise pin the big chart and make those
    /// controls look like they only affect the mini-plots below.</summary>
    private void OnPlotWindowControlClick(object sender, RoutedEventArgs e) => Plot.SetViewRange(-1, -1);

    private void OnSignalsTimelineDragStarted(object sender, DragStartedEventArgs e) => Plot.SetViewRange(-1, -1);

    private void OnSignalsTimelinePreviewMouseDown(object sender, MouseButtonEventArgs e) => Plot.SetViewRange(-1, -1);

    private void OnToggleSignalPlotClick(object sender, RoutedEventArgs e)
    {
        if (SignalsGrid.SelectedItem is PlotSeriesViewModel series)
            _viewModel.Signals.TogglePlot(series);
    }

    /// <summary>Explicit, discoverable action for plotting the selected
    /// physical source. Never silently plot the first search match when the
    /// selection is absent or ambiguous.</summary>
    private void OnPlotSelectedSignalClick(object sender, RoutedEventArgs e)
    {
        // Use the actual selected PlotSeriesViewModel, rather than trying
        // to reconstruct an ambiguous message/name/DBC/channel key. J1939
        // PGNs may have multiple physically distinct source addresses.
        var series = _viewModel.Signals.SelectedSeries;
        if (series is null || !_viewModel.Signals.AllSeries.Any(item => ReferenceEquals(item, series)))
        {
            _viewModel.Signals.ReportStatus("Select a recorded CAN signal to add it to the main plot.");
            return;
        }
        if (_viewModel.Signals.HasMultipleLoggers && series.Timeline.OriginalSourceIndex >= 0)
            _viewModel.Signals.SelectedLoggerIndex = series.Timeline.OriginalSourceIndex;

        series.IsPlotted = true; // idempotent; never discards other curves
        if (series.Samples.Count > 0)
        {
            Plot.ResetZoom();
            _viewModel.Signals.SeekToTimestamp(series.Samples[^1].TimestampNanoseconds);
        }
        _viewModel.StatusText = $"Added exact {series.RecordingText} / " +
            $"{series.ChannelText} / 0x{series.IdentifierHex} / " +
            $"{series.MessageName}.{series.SignalName} to the persistent plot.";
    }

    /// <summary>The ▸/▾ button in the Details column — the explicit way to open/close a trace
    /// row's inline decoded detail. Plain clicks anywhere else (rows, scrollbars) never toggle
    /// it, so scrolling can't accidentally close an open panel.</summary>
    private void OnToggleTraceRowDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TraceRowViewModel row })
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }

    /// <summary>Double-click on a trace row is a friendlier alias for the Details arrow. Ignored
    /// inside the expanded detail area (its own double-click opens a signal chart) and on any
    /// in-row button.</summary>
    private void OnTraceGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var origin = e.OriginalSource as DependencyObject;
        if (FindAncestorSafe<ButtonBase>(origin) is not null ||
            FindAncestorSafe<System.Windows.Controls.Primitives.DataGridDetailsPresenter>(origin) is not null)
        {
            return;
        }

        if (FindAncestorSafe<DataGridRow>(origin)?.Item is TraceRowViewModel row)
        {
            row.IsExpanded = !row.IsExpanded;
            e.Handled = true;
        }
    }

    /// <summary>Keeps the mouse wheel scrolling the trace fluidly while the pointer is over an
    /// expanded detail card. The only element allowed to consume the wheel is the decoded-signal
    /// table when it actually has something to scroll; everything else forwards to the grid so
    /// the panel never becomes a scrolling dead zone.</summary>
    private void OnTraceDetailPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = FindAncestorSafe<ScrollViewer>(e.OriginalSource as DependencyObject, (DependencyObject)sender);
        if (scrollViewer is { ScrollableHeight: > 0 } &&
            ((e.Delta < 0 && scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight) ||
             (e.Delta > 0 && scrollViewer.VerticalOffset > 0)))
        {
            return;
        }

        var traceScrollViewer = FindVisualDescendant<ScrollViewer>(TraceGrid);
        if (traceScrollViewer is null)
        {
            return;
        }

        e.Handled = true;
        var steps = Math.Max(1, Math.Abs(e.Delta) / 40);
        for (var index = 0; index < steps; index++)
        {
            if (e.Delta > 0)
            {
                traceScrollViewer.LineUp();
            }
            else
            {
                traceScrollViewer.LineDown();
            }
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    /// <summary>Ancestor walk that survives non-visual sources (e.g. a Run inside a TextBlock)
    /// and can stop at a given container instead of walking to the window root.</summary>
    private static T? FindAncestorSafe<T>(object? source, DependencyObject? stopAt = null)
        where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            if (ReferenceEquals(current, stopAt))
            {
                return null;
            }

            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>Clicking the already-open row again closes the decoded signal panel below.</summary>
    private void OnByteMatrixGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ButtonBase>((DependencyObject)e.OriginalSource) is not null)
        {
            return;
        }

        var row = FindAncestor<DataGridRow>((DependencyObject)e.OriginalSource);
        if (row?.Item is ByteMatrixRowViewModel byteMatrixRow &&
            ReferenceEquals(byteMatrixRow, _viewModel.ByteMatrix.SelectedRow))
        {
            _viewModel.ByteMatrix.SelectedRow = null;
            e.Handled = true;
        }
    }

    private void OpenTraceSignalDetail(SignalDetailRow signal)
    {
        var series = _viewModel.Signals.FindObservedSignal(signal.Message, signal.Signal,
            channel: signal.SourceChannel >= 0 ? signal.SourceChannel : null,
            databaseName: string.IsNullOrWhiteSpace(signal.DatabaseName) ? null : signal.DatabaseName,
            identifier: signal.DefinitionIdentifier,
            isExtended: signal.SourceIsExtended,
            sourceIdentifier: signal.FrameIdentifier);
        if (series is not null)
            OpenSignalSeriesDetail(series, signal.SourceTimestampNanoseconds);
    }

    /// <summary>One-click Quick Plot from the observed signal list, without requiring
    /// selection of a Trace row or changing the user's primary workspace.</summary>
    private void OnQuickPlotSeriesClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlotSeriesViewModel series })
            OpenSignalSeriesDetail(series);
    }

    private void OnTracePlotSelectedMessageWithoutNavigationClick(object sender, RoutedEventArgs e) =>
        _viewModel.PlotSelectedTraceMessage(navigateToPlot: false);

    private void OnOpenSignalsTrendClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Investigation.ResetToSimple();
        _viewModel.SelectedNavIndex = MainViewModel.InvestigationNavIndex;
    }

    /// <summary>Discover changes recorded in the SAME physical logger around the
    /// selected signal/time. Background scan reads immutable source timelines; the
    /// UI never labels temporal proximity as a diagnosed cause of a DTC.</summary>
    private async void OnFindNearbySignalChangesClick(object sender, RoutedEventArgs e)
    {
        var focus = _viewModel.Signals.SelectedSeries;
        if (focus is null || focus.Samples.Count == 0)
        {
            _viewModel.StatusText = "Select an observed signal first to find nearby recorded changes.";
            return;
        }

        var instant = _viewModel.PlotCursorNanoseconds;
        if (instant < 0 || instant < focus.Samples[0].TimestampNanoseconds)
            instant = focus.Samples[^1].TimestampNanoseconds;

        var timeAtSelection = instant;
        var seriesSnapshot = _viewModel.Signals.AllSeries.ToArray();
        // Reject duplicate source keys instead of guessing which ambiguous
        // DBC series should be plotted (and never crash a UI callback).
        var lookup = seriesSnapshot.GroupBy(item => item.Timeline.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var cancellation = new CancellationTokenSource();
        var closed = false;
        var scanFinished = false;

        var popup = new Window
        {
            Title = "Nearby CAN signal changes — temporal evidence",
            Width = Math.Min(1010, Math.Max(600, SystemParameters.WorkArea.Width - 45)),
            Height = Math.Min(680, Math.Max(390, SystemParameters.WorkArea.Height - 60)),
            MinWidth = 560,
            MinHeight = 330,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = _viewModel,
            Icon = Icon
        };
        popup.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        TitleBarTheme.Hook(popup);

        var surface = new Grid { Margin = new Thickness(12) };
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = $"Changes near t={timeAtSelection / 1e9:F6}s · {focus.MessageName}.{focus.SignalName}",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(heading, 0);
        surface.Children.Add(heading);
        var explanation = new TextBlock
        {
            Text = "Searching ±5 s, matching the original physical logger. " +
                "Ranked by distance from the cursor; timing alone does NOT prove causality, ECU failure or DID/DTC identity.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(explanation, 1);
        surface.Children.Add(explanation);

        var resultsGrid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true
        };
        resultsGrid.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "investigation.nearbyChangeGrid");
        static DataGridTextColumn Column(string header, string property, double width) =>
            new() { Header = header, Binding = new System.Windows.Data.Binding(property),
                    Width = new DataGridLength(width), IsReadOnly = true };
        resultsGrid.Columns.Add(Column("Observed signal", nameof(NearbySignalCandidateRow.Signal), 190));
        resultsGrid.Columns.Add(Column("Recording / exact source", nameof(NearbySignalCandidateRow.Source), 250));
        resultsGrid.Columns.Add(Column("Time [s]", nameof(NearbySignalCandidateRow.TransitionTime), 98));
        resultsGrid.Columns.Add(Column("Offset from cursor", nameof(NearbySignalCandidateRow.RelativeTime), 120));
        resultsGrid.Columns.Add(Column("Recorded change", nameof(NearbySignalCandidateRow.Values), 145));
        resultsGrid.Columns.Add(Column("Trace frame evidence", nameof(NearbySignalCandidateRow.OriginalFrames), 135));
        Grid.SetRow(resultsGrid, 2);
        surface.Children.Add(resultsGrid);

        var actions = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var closeButton = new Button { Content = "Close", Padding = new Thickness(12, 5, 12, 5) };
        DockPanel.SetDock(closeButton, Dock.Right);
        closeButton.Click += (_, _) => popup.Close();
        actions.Children.Add(closeButton);

        var addSelected = new Button { Content = "Add selected to main plot", Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        DockPanel.SetDock(addSelected, Dock.Left);
        addSelected.Click += (_, _) =>
        {
            if (resultsGrid.SelectedItem is not NearbySignalCandidateRow picked) return;
            picked.Series.IsPlotted = true;
            _viewModel.StatusText = $"Added {picked.Signal} from the recorded source to the main plot. Temporal proximity is not causality.";
        };
        actions.Children.Add(addSelected);

        var addTopFive = new Button { Content = "Add first 5 to main plot", Padding = new Thickness(12, 5, 12, 5),
            IsEnabled = false };
        addTopFive.Click += (_, _) =>
        {
            if (resultsGrid.ItemsSource is not IReadOnlyList<NearbySignalCandidateRow> results) return;
            foreach (var candidate in results.Take(5))
                candidate.Series.IsPlotted = true;
            _viewModel.StatusText = $"Added {Math.Min(5, results.Count)} temporally nearby signals. Review original frame evidence before conclusions.";
        };
        actions.Children.Add(addTopFive);
        resultsGrid.SelectionChanged += (_, _) => addSelected.IsEnabled =
            resultsGrid.SelectedItem is NearbySignalCandidateRow;
        Grid.SetRow(actions, 3);
        surface.Children.Add(actions);
        popup.Content = surface;
        popup.Closed += (_, _) =>
        {
            closed = true;
            if (!scanFinished) cancellation.Cancel();
            else cancellation.Dispose();
        };
        popup.Show();

        try
        {
            var findings = await Task.Run(() => RbgCanInsight.Analysis.NearbySignalChangeFinder.Find(
                seriesSnapshot.Select(item => item.Timeline).ToArray(),
                focus.Timeline, timeAtSelection, cancellationToken: cancellation.Token),
                cancellation.Token);
            if (closed) return;
            // Never show results from a replaced trace or silently match new
            // series that merely reuse the same name/source identifier.
            if (!_viewModel.Signals.AllSeries.Any(item => ReferenceEquals(item, focus)))
            {
                explanation.Text = "The decoded trace changed while searching. Reopen this analysis on the current source.";
                return;
            }
            var rows = findings.Where(hit => lookup.ContainsKey(hit.Timeline.Key))
                .Select(hit => new NearbySignalCandidateRow(lookup[hit.Timeline.Key], hit))
                .ToArray();
            resultsGrid.ItemsSource = rows;
            addTopFive.IsEnabled = rows.Length > 0;
            explanation.Text = rows.Length == 0
                ? "No other decoded signal made a recorded value transition within ±5 s on this logger. This does not rule out an intermittent fault or missing DBC evidence."
                : $"{rows.Length} source-backed nearby signal transitions (best match per signal). " +
                  "Click a row to add that exact signal, or add the first five. Temporal proximity is NOT proof of fault causality.";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            if (!closed) explanation.Text = "Nearby-change analysis failed: " + ex.Message;
        }
        finally
        {
            scanFinished = true;
            if (closed) cancellation.Dispose();
        }
    }

    private void OnPopOutTraceToolsClick(object sender, RoutedEventArgs e)
    {
        TraceAnalysisToolsExpander.IsExpanded = true;
        var popup = Common.PopOutHost.Toggle(TraceAnalysisToolsExpander, this,
            "Trace analysis tools — read-only offline evidence", 1000, 700);
        if (popup is not null)
            popup.Closed += (_, _) => TraceAnalysisToolsExpander.IsExpanded = false;
    }

    private void OnPopOutTriggerEditorClick(object sender, RoutedEventArgs e)
    {
        StatisticsTriggerEditorPanel.Visibility = Visibility.Visible;
        var popup = Common.PopOutHost.Toggle(StatisticsTriggerEditorPanel, this,
            "Signal triggers — source-scoped rule editor", 1000, 690);
        if (popup is not null)
            popup.Closed += (_, _) => StatisticsTriggerEditorPanel.Visibility = Visibility.Collapsed;
    }

    private void OnPopOutStatisticsClick(object sender, RoutedEventArgs e)
    {
        var window = Common.PopOutHost.Toggle(StatisticsReportsPanel, this, "Statistics reports", 1200, 760);
        if (window is null)
            return;
        // The button only makes sense on the page; inside the window it would do nothing useful.
        StatisticsPopOutButton.Visibility = Visibility.Collapsed;
        window.Closed += (_, _) => StatisticsPopOutButton.Visibility = Visibility.Visible;
    }

    /// <summary>Opens the selected UDS transaction's transport, payload and decoded
    /// values in a window (double-click on a transaction row).</summary>
    private void OnUdsTransactionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestorSafe<DataGridRow>(e.OriginalSource) is null)
            return;
        Common.PopOutHost.Toggle(UdsTransactionDetailCard, this, "UDS transaction detail", 1100, 560);
    }

    private (string, string, System.Data.DataTable) PickA2lXcpReport(IReadOnlyList<RbgCanInsight.Domain.CanFrame> frames)
    {
        var open = new Microsoft.Win32.OpenFileDialog { Title = "Select an A2L (ASAP2) file", Filter = "A2L (*.a2l)|*.a2l|All files (*.*)|*.*" };
        if (open.ShowDialog(this) != true) throw new OperationCanceledException();
        var a2l = RbgCanInsight.Analysis.Xcp.A2l.A2lReader.ReadFile(open.FileName);
        return ("XCP / CCP + A2L", $"{System.IO.Path.GetFileName(open.FileName)}: {a2l.MeasurementCount:N0} measurements, {a2l.CharacteristicCount:N0} characteristics; read-only, addresses are matched exactly",
            Reports.XcpReports.Xcp(frames, a2l));
    }

    private void OnProtocolReportClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string kind })
            return;
        var frames = _viewModel.Frames;
        if (frames.Count == 0)
        {
            _viewModel.StatusText = "Open a CAN trace first.";
            return;
        }

        try
        {
            var (title, note, table) = kind switch
            {
                "j1939" => ("J1939 messages", "Multi-packet reassembly, DM1/DM2 and address claims from recorded 29-bit frames",
                    Reports.ProtocolReports.J1939(frames)),
                "j1939signals" => ("J1939 signals", "Well-known J1939 parameters decoded exactly per SAE J1939-71 (sentinels counted, never scaled)",
                    Reports.ProtocolReports.J1939Signals(frames)),
                "xcp" => ("XCP / CCP", "XCP-on-CAN and CCP decoded offline; identifier pairs are heuristic (see the Detection rows)",
                    Reports.XcpReports.Xcp(frames)),
                "xcpa2l" => PickA2lXcpReport(frames),
                "obd" => ("OBD-II", "OBD-II requests and responses decoded from recorded frames",
                    Reports.ProtocolReports.Obd(frames)),
                "candidates" => ("Counters and checksums", "Candidate counters and checksum bytes (evidence only)",
                    Reports.ProtocolReports.Candidates(frames)),
                "busload" => ("Bus load timeline", $"Per-second load at the assumed {_viewModel.BusLoadRows.FirstOrDefault()?.BitrateKbps ?? 500:0.##} kbit/s (estimated from nominal bits, per-second windows)",
                    Reports.ProtocolReports.BusLoad(frames, _viewModel.BusLoadRows.FirstOrDefault()?.BitrateKbps ?? 500, _viewModel.ImportHealth)),
                "suggest" => ("DBC suggestion", "Loaded DBCs ranked per channel by trace coverage",
                    Reports.ProtocolReports.SuggestDbc(frames, _viewModel.Databases)),
                "dbccoverage" => ("Trace x DBC coverage", "Unmatched IDs, payload bytes no signal covers and out-of-range physical values per loaded DBC",
                    Reports.ProtocolReports.DbcCoverage(frames, _viewModel.Databases)),
                _ => throw new InvalidOperationException("Unknown report " + kind)
            };
            if (kind is "suggest" or "dbccoverage" && _viewModel.Databases.Count == 0)
            {
                _viewModel.StatusText = "Load at least one DBC to compare it with this trace.";
                return;
            }

            new ReportTableWindow(title, note, table) { Owner = this }.Show();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"{kind} analysis failed: {ex.Message}";
        }
    }

    private Analysis.Buses.LdfFile? _linLdf;

    /// <summary>LIN, FlexRay and CAN XL evidence tables (frames come from the import side collection).</summary>
    private void OnBusReportClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string kind })
            return;
        var bus = _viewModel.BusFrames;
        var needsLin = kind.StartsWith("lin", StringComparison.Ordinal);
        var count = needsLin ? bus.Lin.Count : kind == "flexray" ? bus.FlexRay.Count : bus.CanXl.Count;
        if (count == 0)
        {
            var message = !_viewModel.IsTraceLoaded ? "Open a trace first."
                : needsLin ? "No LIN frames in the loaded trace (LIN is read from BLF files)."
                : kind == "flexray" ? "No FlexRay frames in the loaded trace (FlexRay is read from BLF files)."
                : "No CAN XL frames in the loaded trace (CAN XL is read from candump -L logs and SocketCAN pcap/pcapng captures).";
            _viewModel.StatusText = message;
            // A status-bar line is easy to miss after a click, so say it where the user is looking.
            MessageBox.Show(this, message, needsLin ? "LIN tools" : kind == "flexray" ? "FlexRay tools" : "CAN XL tools",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (kind == "linsignals")
            {
                var open = new Microsoft.Win32.OpenFileDialog { Title = "Select a LIN Description File", Filter = "LIN description (*.ldf)|*.ldf|All files (*.*)|*.*" };
                if (open.ShowDialog(this) != true)
                    return;
                _linLdf = Analysis.Buses.LdfParser.Parse(System.IO.File.ReadAllText(open.FileName));
            }

            var (title, note, table) = kind switch
            {
                "linframes" => ("LIN frames", "Recorded LIN frames with PID parity and classic/enhanced checksum verification",
                    Reports.BusReports.LinFrames(bus.Lin, _linLdf)),
                "lintiming" => ("LIN cycle timing", "Observed period per LIN frame ID",
                    Reports.BusReports.LinTiming(bus.Lin)),
                "lindiag" => ("LIN diagnostics", "Node configuration and UDS-on-LIN transport reassembly (frames 0x3C/0x3D)",
                    Reports.BusReports.LinDiagnostics(bus.Lin)),
                "linsignals" => ("LIN signals (LDF)", "Signals decoded with the loaded LIN Description File",
                    Reports.BusReports.LinSignals(_linLdf!, bus.Lin)),
                "flexray" => ("FlexRay slot/cycle matrix", "Slots, cycles, inferred missing slots and recorder-flagged errors",
                    Reports.BusReports.FlexRayMatrix(bus.FlexRay)),
                "canxl" => ("CAN XL frames", "CAN XL frame list (SDT raw; names only for verified values)",
                    Reports.BusReports.CanXlList(bus.CanXl)),
                _ => throw new InvalidOperationException("Unknown report " + kind)
            };
            new ReportTableWindow(title, note, table) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"{kind} analysis failed: {ex.Message}";
        }
    }

    private void OnAnalyzeSignalClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Signals.SelectedSeries is not { } series)
        {
            _viewModel.StatusText = "Select a recorded signal row first, then choose Signal analysis.";
            return;
        }

        try
        {
            var table = Reports.ProtocolReports.SignalAnalysis(series.SignalName, series.Samples);
            new ReportTableWindow($"Signal analysis - {series.SignalName}",
                "Statistics, histogram and spectrum of the recorded samples", table) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Signal analysis failed: " + ex.Message;
        }
    }

    private async void OnCompareSourcesClick(object sender, RoutedEventArgs e)
    {
        var report = await _viewModel.BuildSourceCoverageAsync();
        if (report is null) return;
        new TraceSourceCoverageWindow(report) { Owner = this }.ShowDialog();
    }

    private async void OnMergeTracesClick(object sender, RoutedEventArgs e)
    {
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select two or more traces to merge",
            Filter = TraceIO.TraceImporter.FileDialogFilter,
            Multiselect = true
        };
        _viewModel.ClearStaleStatus();
        if (open.ShowDialog(this) != true)
            return; // cancelled: nothing to report
        if (open.FileNames.Length < 2)
        {
            _viewModel.StatusText = "Select at least two trace files to merge.";
            return;
        }

        if (TraceMergeModeDialog.Ask(this, open.FileNames.Length) is not { } mergeMode)
            return;

        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save merged trace",
            Filter = "Vector ASCII (*.asc)|*.asc|CSV (*.csv)|*.csv|PCAN trace (*.trc)|*.trc|Vector binary log (*.blf)|*.blf",
            DefaultExt = ".asc",
            FileName = "merged.asc"
        };
        if (save.ShowDialog(this) != true)
            return;

        try
        {
            var sources = new List<IReadOnlyList<Domain.CanFrame>>();
            foreach (var file in open.FileNames)
            {
                var list = new List<Domain.CanFrame>();
                await foreach (var frame in TraceIO.TraceImporter.ReadAsync(file))
                    list.Add(frame);
                list.Sort((x, y) => x.TimestampNanoseconds.CompareTo(y.TimestampNanoseconds));
                sources.Add(list);
            }

            var merged = TraceIO.TraceTools.MergeSession(sources, mergeMode).Frames;
            var ext = System.IO.Path.GetExtension(save.FileName).ToLowerInvariant();
            if (ext == ".csv") await TraceIO.TraceExporter.WriteCsvAsync(save.FileName, merged);
            else if (ext == ".trc") await TraceIO.TraceExporter.WriteTrcAsync(save.FileName, merged);
            else if (ext == ".blf") await TraceIO.TraceExporter.WriteBlfAsync(save.FileName, merged);
            else await TraceIO.TraceExporter.WriteAscAsync(save.FileName, merged);
            _viewModel.StatusText = $"Merged {open.FileNames.Length} traces into {merged.Count:N0} frames: {System.IO.Path.GetFileName(save.FileName)}. Original files were not modified.";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Merge failed: " + ex.Message;
        }
    }

    /// <summary>The optional analysis tabs and quick-focus bar start collapsed to give the trace rows priority.</summary>
    private bool _traceSecondaryUserChoice;

    private void OnToggleTraceSecondaryClick(object sender, RoutedEventArgs e)
    {
        _traceSecondaryUserChoice = TraceNotebookPanel.Visibility != Visibility.Visible;
        ApplyTraceSecondaryVisibility();
    }

    /// <summary>Keep the optional analysis tabs and quick-focus bar collapsed until explicitly requested.</summary>
    private void ApplyTraceSecondaryVisibility()
    {
        var show = _traceSecondaryUserChoice;
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TraceNotebookPanel.Visibility = visibility;
        TraceQuickFocusPanel.Visibility = visibility;
        TraceSecondaryToggleButton.Content = show ? "Tabs & focus ▴" : "Tabs & focus ▾";
    }

    private bool _navCompact;

    /// <summary>On short windows tighten the navigation rail (smaller item padding, no tagline / help button — Help stays on F1 and in
    /// "Session &amp; tools") so every workspace stays reachable without scrolling.</summary>
    private void OnNavRailSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = NavRailGrid.ActualHeight > 0 && NavRailGrid.ActualHeight < 760;
        if (compact == _navCompact) return;
        _navCompact = compact;
        Resources["NavItemPadding"] = compact ? new Thickness(11, 6, 14, 6) : new Thickness(11, 10, 14, 10);
        NavTagline.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NavHelpButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnToggleAnalysisToolsClick(object sender, RoutedEventArgs e) =>
        TraceAnalysisToolsExpander.IsExpanded = !TraceAnalysisToolsExpander.IsExpanded;

    private void OnE2eCheckClick(object sender, RoutedEventArgs e) => E2eCheckWindow.Open(this, _viewModel);

    private void OnTraceToolsClick(object sender, RoutedEventArgs e) => TraceToolsWindow.Open(this, _viewModel);

    private void OnNotesClick(object sender, RoutedEventArgs e) => AnnotationsWindow.Open(this, _viewModel);

    private void OnProjectSaveClick(object sender, RoutedEventArgs e) => Projects.ProjectCommands.SaveProject(this, _viewModel);

    private void OnProjectOpenClick(object sender, RoutedEventArgs e) => Projects.ProjectCommands.OpenProject(this, _viewModel);

    private void OnOpenDerivedSignals(object sender, RoutedEventArgs e) => DerivedSignalsWindow.Open(this, _viewModel);

    private void OnSignalChartsClick(object sender, RoutedEventArgs e) =>
        SignalChartsWindow.Open(this, _viewModel, _viewModel.Signals.SelectedSeries);

    private void OnExportOdxClick(object sender, RoutedEventArgs e) => Projects.CatalogExportCommands.ExportOdx(this, _viewModel);

    private DbcInspectorWindow? _dbcInspectorWindow;

    private void OnOpenDbcInspectorClick(object sender, RoutedEventArgs e)
    {
        if (_dbcInspectorWindow is { IsLoaded: true })
        {
            _dbcInspectorWindow.Activate();
            return;
        }
        _dbcInspectorWindow = new DbcInspectorWindow(this, _viewModel) { Owner = this };
        _dbcInspectorWindow.Closed += (_, _) => _dbcInspectorWindow = null;
        _dbcInspectorWindow.Show();
    }

    private void OnDbcQuickPlotClick(object sender, RoutedEventArgs e) => OpenDbcQuickPlot();

    /// <summary>DBC definition → recorded signal plot, retaining the actual
    /// 11-/29-bit ID and the inspector's explicitly selected CAN channel.</summary>
    internal void OpenDbcQuickPlot()
    {
        var selected = _viewModel.SelectedDbcSignal;
        if (selected is null)
            return;

        var hex = selected.Identifier.TrimEnd('x', 'X');
        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var identifier))
            return;

        var isExtended = selected.Identifier.EndsWith("x", StringComparison.OrdinalIgnoreCase);
        var series = _viewModel.Signals.FindObservedSignal(
            selected.Message, selected.Signal,
            channel: _viewModel.DbcInspector.SelectedPhysicalChannel,
            databaseName: selected.Database, identifier: identifier, isExtended: isExtended);
        if (series is null)
        {
            if (selected.ObservedSamples == 0)
            {
                // A DBC declaration is not proof the signal was transmitted,
                // decoded or activated in this recorded trace.
                _viewModel.StatusText =
                    $"No recorded samples for {selected.Database}/{selected.Message}.{selected.Signal}. " +
                    "Check the trace, assigned CAN channel, frame format and any multiplex condition.";
                return;
            }

            // Do not pick an arbitrary physical bus when identically named
            // observed series exist, or when the chosen inspector channel
            // does not contain this definition.
            _viewModel.StatusText =
                $"The DBC signal has {selected.ObservedSamples:N0} recorded samples, " +
                "but its selected CAN source does not resolve uniquely. " +
                "Choose the exact DBC/channel/ID in Signals & Plot.";
            _viewModel.Signals.SearchText = selected.Signal;
            _viewModel.SelectedNavIndex = 3;
            return;
        }

        OpenSignalSeriesDetail(series);
    }

    private void OpenSignalSeriesDetail(PlotSeriesViewModel series, long initialTimestampNanoseconds = -1)
    {
        var window = new SignalDetailWindow(series, initialTimestampNanoseconds)
        {
            Owner = this,
            DetailLinearInterpolation = _viewModel.Signals.LinearInterpolation
        };
        _openQuickPlots.Add(window);
        window.Closed += (_, _) => _openQuickPlots.Remove(window);
        window.JumpToTraceRequested += (_, frameIndex) =>
        {
            _viewModel.SelectedNavIndex = 1;
            _viewModel.Trace.JumpToFrameIndex(frameIndex);
            Activate();
        };
        window.Show();
    }

    private void OnChangeRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChangesGrid.SelectedItem is ChangeRowViewModel change)
        {
            _viewModel.Matrix.OpenChangeFrame(change);
        }
    }

    private void OnResetZoom(object sender, RoutedEventArgs e) => Plot.ResetZoom();

    private void OnColumnsClick(object sender, RoutedEventArgs e)
    {
        var grid = CurrentLayoutGrid();
        if (grid is null)
        {
            MessageBox.Show("This view has no configurable table columns.", "Columns", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{ViewName(_viewModel.SelectedNavIndex)} columns",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        foreach (var column in grid.Columns.OrderBy(column => column.DisplayIndex))
        {
            var isProtected = IsProtectedColumn(grid, column);
            var checkBox = new CheckBox
            {
                Content = ColumnHeader(column),
                IsChecked = isProtected || column.Visibility == Visibility.Visible,
                IsEnabled = !isProtected,
                Margin = new Thickness(0, 3, 0, 3),
                MinWidth = 220
            };
            checkBox.Checked += (_, _) => column.Visibility = Visibility.Visible;
            checkBox.Unchecked += (_, _) =>
            {
                if (!isProtected)
                {
                    column.Visibility = Visibility.Collapsed;
                }
            };
            panel.Children.Add(checkBox);
        }

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var showAll = new Button { Content = "Show all", MinWidth = 82, Margin = new Thickness(0, 0, 6, 0) };
        var close = new Button { Content = "Close", MinWidth = 82, IsDefault = true };
        buttonRow.Children.Add(showAll);
        buttonRow.Children.Add(close);
        panel.Children.Add(buttonRow);

        var dialog = new Window
        {
            Title = "Columns",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Content = panel,
            Background = TryFindResource("SurfaceBrush") as Brush,
            Foreground = TryFindResource("TextPrimaryBrush") as Brush
        };

        Common.TitleBarTheme.Hook(dialog);

        showAll.Click += (_, _) =>
        {
            foreach (var column in grid.Columns)
            {
                column.Visibility = Visibility.Visible;
            }

            foreach (var checkBox in panel.Children.OfType<CheckBox>())
            {
                checkBox.IsChecked = true;
            }
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => SaveGridLayouts();
        dialog.ShowDialog();
    }

    private void OnExportLayoutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "RBG CAN Insight layout (*.rbglayout)|*.rbglayout|JSON (*.json)|*.json",
            DefaultExt = ".rbglayout",
            FileName = $"rbg-layout-{DateTime.Now:yyyyMMdd-HHmmss}.rbglayout",
            Title = "Export UI layout"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            SaveGridLayouts();
            var package = new UiLayoutPackage(1, _viewModel.SelectedNavIndex, _viewModel.ThemeIndex, BuildGridLayouts());
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Layout export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImportLayoutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "RBG CAN Insight layout (*.rbglayout)|*.rbglayout|JSON (*.json)|*.json|All files (*.*)|*.*",
            Title = "Import UI layout"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var package = JsonSerializer.Deserialize<UiLayoutPackage>(File.ReadAllText(dialog.FileName));
            if (package is null)
            {
                return;
            }

            ApplyGridLayouts(package.Grids);
            _viewModel.SelectedNavIndex = package.SelectedNavIndex;
            SaveGridLayouts();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Layout import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            await _viewModel.HandleDroppedFilesAsync(paths);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static string GridLayoutPath => Path.Combine(AppStorage.ConfigDirectory, "grid-layouts.json");

    private DataGrid? CurrentLayoutGrid() => _viewModel.SelectedNavIndex switch
    {
        1 => TraceGrid,
        3 => SignalsGrid,
        4 => MatrixGrid,
        5 => ByteMatrixGrid,
        6 => StatisticsGrid,
        7 => MappingGrid,
        _ => null
    };

    private static string ViewName(int selectedNavIndex) => selectedNavIndex switch
    {
        1 => "Trace",
        3 => "Signals",
        4 => "Change Matrix",
        5 => "Byte Matrix",
        6 => "Statistics",
        7 => "Mapping",
        _ => "Current view"
    };

    private void LoadGridLayouts()
    {
        try
        {
            if (!File.Exists(GridLayoutPath))
            {
                return;
            }

            var layouts = JsonSerializer.Deserialize<Dictionary<string, GridColumnLayout[]>>(File.ReadAllText(GridLayoutPath));
            if (layouts is null)
            {
                return;
            }

            ApplyGridLayouts(layouts);
        }
        catch
        {
            // Layout persistence is convenience-only.
        }
    }

    private void SaveGridLayouts()
    {
        try
        {
            var layouts = BuildGridLayouts();
            Directory.CreateDirectory(Path.GetDirectoryName(GridLayoutPath)!);
            File.WriteAllText(GridLayoutPath, JsonSerializer.Serialize(layouts, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort.
        }
    }

    private Dictionary<string, GridColumnLayout[]> BuildGridLayouts() =>
        _layoutGrids
            // A grid that was never shown this session (its view was never visited) has never
            // been measured. Skip it entirely so its previously-saved (or default) layout stands
            // rather than being overwritten with meaningless zero/unmeasured values.
            .Where(grid => grid.ActualWidth > 0)
            .ToDictionary(
                grid => grid.Name,
                grid => grid.Columns
                    .Select(column => new GridColumnLayout(
                        ColumnHeader(column),
                        column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value,
                        column.DisplayIndex,
                        IsProtectedColumn(grid, column) || column.Visibility == Visibility.Visible))
                    .ToArray());

    private void ApplyGridLayouts(IReadOnlyDictionary<string, GridColumnLayout[]> layouts)
    {
        foreach (var grid in _layoutGrids)
        {
            if (!layouts.TryGetValue(grid.Name, out var columns))
            {
                continue;
            }

            // A saved layout from an older build of this view (different/renamed columns) only
            // partially matches today's headers. Reordering from a partial match scrambles the
            // columns that DID match instead of leaving them alone, which is what previously
            // looked like "column order got reversed" after an update. When the saved set is not
            // a complete, one-to-one match for the grid's current columns, only restore width and
            // visibility for the headers that do match and leave display order at its XAML default.
            var isCompleteMatch = columns.Length == grid.Columns.Count &&
                columns.All(saved => grid.Columns.Any(current => ColumnHeader(current) == saved.Header));

            // A previously saved *default* Signals column order hid Signal
            // and Value @ cursor behind 8-12 metadata/action columns. Migrate
            // ONLY that recognizably untouched legacy layout. Keep custom
            // analyst column orders, widths and visibility intact.
            var legacySignalsDefault = grid.Name == nameof(SignalsGrid) &&
                columns.Any(saved => saved.Header == "Quick" && saved.DisplayIndex == 0) &&
                columns.Any(saved => saved.Header == "Expand" && saved.DisplayIndex == 1) &&
                columns.Any(saved => saved.Header == "Signal" && saved.DisplayIndex == 8) &&
                columns.Any(saved => saved.Header == "Value @ cursor" && saved.DisplayIndex == 12) &&
                columns.Any(saved => saved.Header == "Last source" && saved.DisplayIndex == 13);
            var restoreColumnOrder = isCompleteMatch && !legacySignalsDefault;

            foreach (var columnLayout in columns.OrderBy(item => item.DisplayIndex))
            {
                var column = grid.Columns.FirstOrDefault(item => ColumnHeader(item) == columnLayout.Header);
                if (column is null)
                {
                    continue;
                }

                // Columns declared as star-width in XAML are meant to keep proportionally filling
                // whatever space is left. Restoring a saved pixel snapshot would freeze one at
                // that size forever regardless of how much room the window actually has, so leave
                // it on its XAML star sizing and only restore visibility/order for it below.
                if (!_proportionalColumns.Contains((grid.Name, columnLayout.Header)))
                {
                    // A saved width smaller than the column's own declared minimum cannot be a
                    // real measurement (e.g. it was captured before the grid was ever measured).
                    // Restoring such a value would shrink the column far below what its content
                    // needs, so ignore it and leave the XAML default width in place instead.
                    // WPF's own DataGridColumn.MinWidth defaults to 20 even when XAML never sets
                    // it, so trusting column.MinWidth alone is not a meaningful floor — always
                    // require at least 40.
                    var minWidth = Math.Max(column.MinWidth, 40);
                    if (columnLayout.Width >= minWidth)
                    {
                        column.Width = new DataGridLength(columnLayout.Width);
                    }
                }
                column.Visibility = columnLayout.IsVisible || IsProtectedColumn(grid, column)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (restoreColumnOrder && !IsProtectedColumn(grid, column))
                {
                    column.DisplayIndex = Math.Clamp(columnLayout.DisplayIndex, 0, Math.Max(0, grid.Columns.Count - 1));
                }
            }
        }
    }

    private static string ColumnHeader(DataGridColumn column) => column.Header?.ToString() ?? string.Empty;

    private static bool IsProtectedColumn(DataGrid grid, DataGridColumn column)
    {
        var header = ColumnHeader(column);
        var index = grid.Columns.IndexOf(column);
        if (index >= 0 && index < 2)
        {
            return true;
        }

        return header is "" or "Actions" or "Details" or "Plot" or "Open" or "Review";
    }
}
