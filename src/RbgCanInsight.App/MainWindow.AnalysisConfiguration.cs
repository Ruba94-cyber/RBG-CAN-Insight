using System.ComponentModel;
using System.IO;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Win32;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.App;

public partial class MainWindow
{
    private long _analysisConfigurationLoadGeneration;
    private sealed record ConfigurationSignalOption(PlotSeriesViewModel Series, string Display);

    private sealed class ConfigurationPreviewRow : INotifyPropertyChanged
    {
        private ConfigurationSignalOption? _selectedOption;

        public ConfigurationPreviewRow(
            NativeBindingResolution resolution,
            IReadOnlyList<ConfigurationSignalOption> options)
        {
            Resolution = resolution;
            Options = options;
            if (resolution.Status == NativeBindingStatus.Matched && resolution.Timeline is not null)
                _selectedOption = options.FirstOrDefault(option =>
                    ReferenceEquals(option.Series.Timeline, resolution.Timeline));
        }

        public NativeBindingResolution Resolution { get; }
        public NativeSignalBinding Binding => Resolution.Binding;
        public string SavedSignal => Binding.Name + " · " + Binding.Signal.MessageName + "." + Binding.Signal.SignalName +
            $" · line {Binding.LineStyle}";
        public string Status => Resolution.Status switch
        {
            NativeBindingStatus.Matched => "Exact, unique match",
            NativeBindingStatus.Ambiguous => $"Ambiguous ({Resolution.CandidateCount}); choose manually",
            _ when Resolution.Reason == NativeMissingReason.DefinitionChanged =>
                $"Missing: a signal with the same name exists but its DBC definition changed ({Resolution.SameNameCandidates?.Count ?? 0} candidate(s)); choose manually or leave unmapped",
            _ => "Missing; choose manually or leave unmapped"
        };
        public IReadOnlyList<ConfigurationSignalOption> Options { get; }
        public ConfigurationSignalOption? SelectedOption
        {
            get => _selectedOption;
            set
            {
                if (ReferenceEquals(_selectedOption, value)) return;
                _selectedOption = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOption)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private async void OnSaveNativeAnalysisConfigurationClick(object sender, RoutedEventArgs e)
    {
        var watchedKeys = _viewModel.WatchSignals
            .Select(row => row.Series.Timeline.Key)
            .ToHashSet(StringComparer.Ordinal);
        var selected = _viewModel.Signals.AllSeries
            .Where(series => series.IsPlotted || watchedKeys.Contains(series.Timeline.Key))
            .ToArray();
        if (selected.Length == 0)
        {
            _viewModel.StatusText = "Plot or watch at least one decoded signal before saving an analysis configuration.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save native analysis configuration",
            Filter = "RBG analysis configuration (*.rbgconfig)|*.rbgconfig",
            DefaultExt = ".rbgconfig",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "analysis.rbgconfig"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bindings = selected.Select((series, index) =>
            {
                var timeline = series.Timeline;
                var name = $"{timeline.DatabaseName} · CAN {timeline.Channel + 1} · {timeline.Message.Name}.{timeline.Signal.Name}";
                if (!usedNames.Add(name))
                {
                    name += $" · {timeline.SourceIdentifier:X}{(timeline.SourceIsExtended ? "x" : "s")} · {index + 1}";
                    usedNames.Add(name);
                }
                return new NativeSignalBinding(name, NativeSignalReference.FromTimeline(timeline),
                    series.IsPlotted, watchedKeys.Contains(timeline.Key), series.LineStyle);
            }).ToArray();

            var configurationName = Path.GetFileNameWithoutExtension(dialog.FileName);
            // Source files are hashed off the UI thread (SHA-256 + size, so a later load can tell "same / modified / missing").
            var configurationDirectory = Path.GetDirectoryName(Path.GetFullPath(dialog.FileName)) ?? string.Empty;
            var sources = await Task.Run(() => _viewModel.CaptureConfigurationSources(configurationDirectory));
            var extras = _viewModel.CaptureConfigurationState(sources, out var markersOmitted);
            new NativeAnalysisConfigurationStore(dialog.FileName).Write(new NativeAnalysisConfiguration(
                NativeAnalysisConfigurationStore.CurrentSchemaVersion,
                configurationName,
                new NativePlotSettings(_viewModel.Signals.PlotTimeWindowSeconds,
                    _viewModel.Signals.YAxisModeIndex,
                    _viewModel.Signals.ShowInlineLegend,
                    _viewModel.Signals.LinearInterpolation),
                bindings,
                extras));
            _viewModel.StatusText = $"Saved {bindings.Length:N0} signal binding(s), {sources.Count:N0} source file reference(s), " +
                $"{extras.MarkerList.Count:N0} marker(s) and the current filters/cursor to {Path.GetFileName(dialog.FileName)}." +
                (markersOmitted > 0 ? $" {markersOmitted:N0} marker(s) were NOT saved (limit 5,000)." : string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, exception.Message, "Analysis configuration not saved", MessageBoxButton.OK, MessageBoxImage.Error);
            _viewModel.StatusText = "Analysis configuration was not saved.";
        }
    }

    private async void OnLoadNativeAnalysisConfigurationClick(object sender, RoutedEventArgs e)
    {
        // A later load supersedes any pending resolution even when its picker
        // is cancelled or the selected file is invalid.
        var generation = ++_analysisConfigurationLoadGeneration;
        var dialog = new OpenFileDialog
        {
            Title = "Load native analysis configuration",
            Filter = "Native or Vector configuration (*.rbgconfig;*.cfg)|*.rbgconfig;*.cfg|RBG analysis configuration (*.rbgconfig)|*.rbgconfig|Vector configuration candidate (*.cfg)|*.cfg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        if (VectorCanalyzerCfgProbe.IsUnsupportedCandidate(dialog.FileName))
        {
            var message = VectorCanalyzerCfgProbe.DescribeUnsupported(dialog.FileName);
            MessageBox.Show(this, message, "Vector configuration not imported", MessageBoxButton.OK, MessageBoxImage.Information);
            _viewModel.StatusText = "Vector .cfg configuration is unsupported; no contents were read.";
            return;
        }

        NativeAnalysisConfiguration configuration;
        try
        {
            // A schema 1 file is upgraded in memory (nothing is lost); newer schemas are rejected with a clear message.
            configuration = new NativeAnalysisConfigurationStore(dialog.FileName).Read().UpgradedToCurrent();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, exception.Message, "Analysis configuration not loaded", MessageBoxButton.OK, MessageBoxImage.Error);
            _viewModel.StatusText = "Analysis configuration could not be read; the current plot and watch list were kept.";
            return;
        }

        var sourceFrames = _viewModel.Frames;
        var sourceSeries = _viewModel.Signals.AllSeries;
        var timelines = sourceSeries.Select(series => series.Timeline).ToArray();
        bool IsCurrent() => generation == _analysisConfigurationLoadGeneration &&
            ReferenceEquals(sourceFrames, _viewModel.Frames) &&
            ReferenceEquals(sourceSeries, _viewModel.Signals.AllSeries);
        _viewModel.StatusText = "Resolving saved signal identities…";
        IReadOnlyList<NativeBindingResolution> resolutions;
        try
        {
            resolutions = await Task.Run(() => NativeAnalysisConfigurationResolver.Resolve(configuration, timelines));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
        {
            if (!IsCurrent()) return;
            MessageBox.Show(this, exception.Message, "Analysis configuration could not be resolved", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!IsCurrent())
        {
            _viewModel.StatusText = "Configuration not applied: the recording or signal catalog changed.";
            return;
        }

        var options = sourceSeries
            .Select(series => new ConfigurationSignalOption(series,
                $"{series.Timeline.DatabaseName} · {series.RecordingText} · {series.RecordingPath} · CAN {series.Timeline.Channel + 1} · " +
                $"wire 0x{series.IdentifierHex} ({(series.Timeline.SourceIsExtended ? "29-bit" : "11-bit")}) · " +
                $"DBC 0x{series.DefinitionIdentifierHex} ({(series.Timeline.Message.IsExtended ? "29-bit" : "11-bit")}) · " +
                $"{series.MessageName}.{series.SignalName} · {series.Unit}"))
            .ToArray();
        var rows = resolutions.Select(resolution => new ConfigurationPreviewRow(resolution, options)).ToArray();
        // Saved source files: same / modified / missing, reported before anything is applied (no silent mismatch).
        var configurationDirectory = Path.GetDirectoryName(Path.GetFullPath(dialog.FileName)) ?? string.Empty;
        string sourceNotes;
        try
        {
            var checks = await Task.Run(() => _viewModel.CheckConfigurationSources(configuration.ExtrasOrEmpty, configurationDirectory));
            var problems = checks.Where(check => check.State != NativeFileState.Ok).Select(check => check.Describe()).ToArray();
            sourceNotes = checks.Count == 0 ? string.Empty
                : problems.Length == 0 ? $"All {checks.Count:N0} saved source file(s) match (same size and SHA-256)."
                : "Saved source files with problems: " + string.Join(" | ", problems);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            sourceNotes = "Saved source files could not be checked: " + exception.Message;
        }
        if (!IsCurrent())
        {
            _viewModel.StatusText = "Configuration not applied: the recording or signal catalog changed.";
            return;
        }
        if (!ShowConfigurationPreview(configuration.Name, configuration.PlotSettings, rows, sourceNotes)) return;
        if (!IsCurrent())
        {
            _viewModel.StatusText = "Configuration not applied: the recording or signal catalog changed.";
            return;
        }

        var resolved = rows.Where(row => row.SelectedOption is not null).ToArray();
        if (resolved.Length == 0)
        {
            _viewModel.StatusText = "No signal bindings were selected; the current plot and watch list were kept.";
            return;
        }

        NativeSignalBinding[] remappedBindings;
        try
        {
            remappedBindings = rows.Select(row => row.SelectedOption is { } choice
                ? row.Binding with { Signal = NativeSignalReference.FromTimeline(choice.Series.Timeline) }
                : row.Binding).ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
        {
            MessageBox.Show(this, exception.Message, "Signal mapping could not be prepared", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var hasManualRemap = rows.Select((row, index) => !Equals(row.Binding.Signal, remappedBindings[index].Signal)).Any(changed => changed);
        var savedUpdatedMapping = false;
        if (hasManualRemap)
        {
            var save = new SaveFileDialog
            {
                Title = "Save configuration with manual signal mappings",
                Filter = "RBG analysis configuration (*.rbgconfig)|*.rbgconfig",
                DefaultExt = ".rbgconfig",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = Path.GetFileNameWithoutExtension(dialog.FileName) + "-mapped.rbgconfig",
                InitialDirectory = Path.GetDirectoryName(dialog.FileName)
            };
            if (save.ShowDialog(this) == true)
            {
                try
                {
                    new NativeAnalysisConfigurationStore(save.FileName).Write(configuration with { Bindings = remappedBindings });
                    savedUpdatedMapping = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
                {
                    MessageBox.Show(this, exception.Message, "Updated mapping not saved", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        if (!IsCurrent())
        {
            _viewModel.StatusText = "Configuration not applied: the recording or signal catalog changed.";
            return;
        }

        _viewModel.Signals.PlotTimeWindowSeconds = configuration.PlotSettings.TimeWindowSeconds;
        _viewModel.Signals.YAxisModeIndex = configuration.PlotSettings.YAxisModeIndex;
        _viewModel.Signals.ShowInlineLegend = configuration.PlotSettings.ShowInlineLegend;
        _viewModel.Signals.LinearInterpolation = configuration.PlotSettings.LinearInterpolation;
        var restoredState = _viewModel.ApplyConfigurationState(configuration.ExtrasOrEmpty);

        var addedPlots = 0;
        var addedWatches = 0;
        foreach (var row in resolved)
        {
            var series = row.SelectedOption!.Series;
            if (row.Binding.IsPlotted && !series.IsPlotted)
            {
                series.IsPlotted = true;
                addedPlots++;
            }
            series.LineStyle = row.Binding.LineStyle;
            if (row.Binding.IsWatched && !_viewModel.WatchSignals.Any(watch => watch.Series.Timeline.Key == series.Timeline.Key))
            {
                var watch = new WatchSignalRow(series);
                watch.Update(_viewModel.PlotCursorNanoseconds);
                _viewModel.WatchSignals.Add(watch);
                addedWatches++;
            }
        }

        var unresolved = rows.Count(row => row.SelectedOption is null);
        var manualSelections = rows.Count(row => row.Resolution.Status != NativeBindingStatus.Matched && row.SelectedOption is not null);
        _viewModel.StatusText = $"Applied {resolved.Length:N0} explicit signal mapping(s): {addedPlots:N0} curve(s) added, {addedWatches:N0} watch(es) added; " +
            $"{unresolved:N0} unresolved binding(s) skipped. Existing curves and watches were preserved." +
            (restoredState.Length > 0 ? " Also: " + restoredState + "." : string.Empty) +
            (hasManualRemap ? savedUpdatedMapping ? " Manual mappings saved to a new configuration." : " Manual mappings were applied but not saved." : string.Empty) +
            (manualSelections > 0 && !hasManualRemap
                ? " Some manual selections share an indistinguishable saved identity; they apply only to this trace and remain ambiguous on a later load."
                : string.Empty);
    }

    private bool ShowConfigurationPreview(
        string configurationName,
        NativePlotSettings plotSettings,
        IReadOnlyList<ConfigurationPreviewRow> rows,
        string sourceNotes = "")
    {
        var exact = rows.Count(row => row.Resolution.Status == NativeBindingStatus.Matched);
        var ambiguous = rows.Count(row => row.Resolution.Status == NativeBindingStatus.Ambiguous);
        var missing = rows.Count(row => row.Resolution.Status == NativeBindingStatus.Missing);
        var window = new Window
        {
            Title = $"Preview analysis configuration — {configurationName}",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 900,
            Height = 560,
            MinWidth = 680,
            MinHeight = 400,
            ResizeMode = ResizeMode.CanResize
        };
        AutomationProperties.SetAutomationId(window, "analysisConfiguration.preview");
        var panel = new DockPanel { Margin = new Thickness(12) };
        var summary = new TextBlock
        {
            Text = $"{exact:N0} exact unique · {ambiguous:N0} ambiguous · {missing:N0} missing. " +
                   $"Saved plot settings: {plotSettings.TimeWindowSeconds:g} s, Y axis {plotSettings.YAxisModeIndex}, " +
                   $"inline legend {plotSettings.ShowInlineLegend}, interpolation {plotSettings.LinearInterpolation}. " +
                   "Choose a target explicitly for ambiguous or missing entries; blank entries are skipped. Apply adds selections and preserves current curves.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);
        if (!string.IsNullOrWhiteSpace(sourceNotes))
        {
            var sources = new TextBlock { Text = sourceNotes, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetAutomationId(sources, "analysisConfiguration.preview.sources");
            DockPanel.SetDock(sources, Dock.Top);
            panel.Children.Add(sources);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var apply = new Button { Content = "Apply selected", MinWidth = 120, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(apply, "analysisConfiguration.preview.apply");
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "analysisConfiguration.preview.cancel");
        var grid = new DataGrid
        {
            ItemsSource = rows,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        AutomationProperties.SetAutomationId(grid, "analysisConfiguration.preview.grid");
        apply.Click += (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            window.DialogResult = true;
        };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Saved binding",
            Binding = new Binding(nameof(ConfigurationPreviewRow.SavedSignal)),
            IsReadOnly = true,
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Resolution",
            Binding = new Binding(nameof(ConfigurationPreviewRow.Status)),
            IsReadOnly = true,
            Width = new DataGridLength(1.3, DataGridLengthUnitType.Star)
        });
        var comboFactory = new FrameworkElementFactory(typeof(ComboBox));
        comboFactory.SetValue(ComboBox.DisplayMemberPathProperty, nameof(ConfigurationSignalOption.Display));
        comboFactory.SetValue(AutomationProperties.AutomationIdProperty, "analysisConfiguration.preview.target");
        comboFactory.SetValue(ComboBox.IsTextSearchEnabledProperty, true);
        comboFactory.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ConfigurationPreviewRow.Options)));
        comboFactory.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(ConfigurationPreviewRow.SelectedOption))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Explicit target (blank = skip)",
            CellTemplate = new DataTemplate { VisualTree = comboFactory },
            Width = new DataGridLength(3, DataGridLengthUnitType.Star)
        });
        panel.Children.Add(grid);
        window.Content = panel;
        return window.ShowDialog() == true;
    }
}
