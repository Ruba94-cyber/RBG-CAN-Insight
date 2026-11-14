using RbgCanInsight.Domain;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.Controls;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App.Views;

/// <summary>Explore pairwise observational association against ONE selected
/// recorded signal. It never edits DBC mappings or presents lag as a causal
/// relationship. The chart is a preview and does not alter the main plot.</summary>
public sealed class SignalRelationshipsWindow : Window
{
    private readonly DataGrid _grid;
    private readonly TextBlock _detail;
    private readonly SignalPlotControl _preview;
    private readonly IReadOnlyList<SignalRelationship> _all;
    private readonly IReadOnlyDictionary<string, PlotSeriesViewModel> _exact;
    private readonly PlotSeriesViewModel _reference;
    private SignalRelationship? Selected => _grid.SelectedItem as SignalRelationship;

    public SignalRelationshipsWindow(
        Window owner, SignalRelationshipReport report,
        PlotSeriesViewModel reference,
        IReadOnlyDictionary<string, PlotSeriesViewModel> exactSeries,
        Func<bool> isCurrent,
        Action<PlotSeriesViewModel> addToPlot,
        Action<int> openFrame)
    {
        Owner = owner;
        Icon = owner.Icon;
        Title = $"Explore related signals — {reference.MessageName}.{reference.SignalName}";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(1170, SystemParameters.WorkArea.Width - 28);
        Height = Math.Min(770, SystemParameters.WorkArea.Height - 28);
        MinWidth = 580;
        MinHeight = 380;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        TitleBarTheme.Hook(this);
        _reference = reference;
        _all = report.Results;
        _exact = exactSeries;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        intro.Children.Add(new TextBlock
        {
            Text = $"Reference: {reference.RecordingText} · {reference.ChannelText} · " +
                $"CAN 0x{reference.IdentifierHex} · {reference.MessageName}.{reference.SignalName}",
            FontSize = 17, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        intro.Children.Add(new TextBlock
        {
            Text = report.Summary + Environment.NewLine +
                "Two clues: co-varying values (Pearson r) or repeated signal changes preceding " +
                "other changes. A negative r means opposite movement; 0 means no linear association. " +
                "A measured lead is NOT proof of control, dependency or causality." +
                Environment.NewLine + EvidenceKinds.Legend,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 5, 0, 0)
        });
        Grid.SetRow(intro, 0); root.Children.Add(intro);

        var searchPanel = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var reset = new Button { Content = "Reset", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(7, 0, 0, 0) };
        DockPanel.SetDock(reset, Dock.Right); searchPanel.Children.Add(reset);
        var search = new TextBox
        {
            MinWidth = 200,
            ToolTip = "Search signal, CAN ID, bus, direction or source among verified related candidates."
        };
        search.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "relations.search");
        searchPanel.Children.Add(search);
        Grid.SetRow(searchPanel, 1); root.Children.Add(searchPanel);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 200 });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 230 });
        Grid.SetRow(split, 2); root.Children.Add(split);

        _grid = new DataGrid
        {
            ItemsSource = _all,
            SelectionMode = DataGridSelectionMode.Single,
            IsReadOnly = true,
            CanUserSortColumns = true,
            AutoGenerateColumns = false,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            FontSize = 11.5
        };
        _grid.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "relations.grid");
        static DataGridTextColumn Col(string title, string property, int width) =>
            new() { Header = title, Binding = new Binding(property), Width = new DataGridLength(width) };
        _grid.Columns.Add(Col("Recorded signal", nameof(SignalRelationship.SignalText), 190));
        _grid.Columns.Add(Col("Relation observed", nameof(SignalRelationship.AssociationType), 175));
        _grid.Columns.Add(Col("What to inspect next", nameof(SignalRelationship.SuggestedNextCheck), 310));
        _grid.Columns.Add(Col("Original CAN source", nameof(SignalRelationship.SourceText), 235));
        _grid.Columns.Add(Col("r level", nameof(SignalRelationship.LevelText), 75));
        _grid.Columns.Add(Col("r changes", nameof(SignalRelationship.ChangeText), 110));
        _grid.Columns.Add(Col("Observed lead/follow", nameof(SignalRelationship.LeadText), 275));
        _grid.Columns.Add(Col("Pairs", nameof(SignalRelationship.PairedSamples), 64));
        Grid.SetColumn(_grid, 0); split.Children.Add(_grid);
        var splitter = new GridSplitter
        {
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        Grid.SetColumn(splitter, 1); split.Children.Add(splitter);

        var right = new Grid { Margin = new Thickness(5, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
        _detail = new TextBlock
        {
            Text = "Select one relationship to preview two recorded curves.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 7),
            MaxHeight = 90
        };
        Grid.SetRow(_detail, 0); right.Children.Add(_detail);
        _preview = new SignalPlotControl
        {
            ItemsSource = new[] { _reference },
            IgnorePlottedFilter = true,
            YAxisModeIndex = 2,
            WindowEndTimestampNanoseconds = report.WindowEndNanoseconds,
            GlobalStartTimestampNanoseconds = report.WindowStartNanoseconds,
            GlobalEndTimestampNanoseconds = report.WindowEndNanoseconds,
            LinearInterpolation = false,
            ShowInlineLegend = true
        };
        _preview.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "relations.previewChart");
        _preview.Loaded += (_, _) =>
            _preview.SetViewRange(report.WindowStartNanoseconds, report.WindowEndNanoseconds);
        var previewBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _preview
        };
        previewBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetRow(previewBorder, 1); right.Children.Add(previewBorder);

        var evidence = new TextBlock
        {
            Text = "No relationship selected. Every source frame is from the original physical logger.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var evidenceScroll = new ScrollViewer
        {
            Content = evidence,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(evidenceScroll, 2); right.Children.Add(evidenceScroll);
        Grid.SetColumn(right, 2); split.Children.Add(right);

        _grid.SelectionChanged += (_, _) =>
        {
            var selected = Selected;
            if (selected is null) return;
            if (!_exact.TryGetValue(selected.Candidate.Key, out var candidate)) return;
            _preview.ItemsSource = new[] { _reference, candidate };
            _detail.Text = $"{selected.SignalText} · {selected.AssociationType}" +
                Environment.NewLine + $"{selected.LeadText} · r level {selected.LevelText} · r Δ {selected.ChangeText}";
            evidence.Text = selected.SuggestedNextCheck + Environment.NewLine + selected.EvidenceText +
                Environment.NewLine + selected.OriginalFrames +
                $" · ref @ {selected.ReferenceTimestampNanoseconds / 1e9:F6}s, " +
                $"candidate @ {selected.CandidateTimestampNanoseconds / 1e9:F6}s." +
                Environment.NewLine + "Values are decoded from recorded CAN, " +
                "not synchronized by any external ECU clock or DTC state.";
        };

        void Filter()
        {
            var query = search.Text.Trim();
            _grid.ItemsSource = query.Length == 0 ? _all :
                _all.Where(row =>
                    row.SignalText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    row.SourceText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    row.LeadText.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            _grid.SelectedIndex = _grid.Items.Count > 0 ? 0 : -1;
        }
        search.TextChanged += (_, _) => Filter();
        reset.Click += (_, _) => search.Clear();

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button Act(string caption, string id, Action action)
        {
            var button = new Button
            {
                Content = caption, Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 7, 6)
            };
            button.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, id);
            button.Click += (_, _) =>
            {
                if (!isCurrent())
                {
                    MessageBox.Show(this, "The recording/DBC source changed. Re-run the association analysis.",
                        "Stale CAN evidence", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                action();
            };
            actions.Children.Add(button);
            return button;
        }
        Act("＋ Add reference", "relations.addReference", () => addToPlot(reference));
        Act("＋ Add related signal", "relations.addRelated", () =>
        {
            if (Selected is { } row && _exact.TryGetValue(row.Candidate.Key, out var candidate))
                addToPlot(candidate);
        });
        Act("＋ Compare both on main plot", "relations.addBoth", () =>
        {
            if (Selected is { } row && _exact.TryGetValue(row.Candidate.Key, out var candidate))
            {
                addToPlot(reference);
                addToPlot(candidate);
            }
        });
        Act("Original reference CAN frame", "relations.openReferenceFrame", () =>
        {
            if (Selected is { } row) openFrame(row.ReferenceFrameIndex);
        });
        Act("Original related CAN frame", "relations.openCandidateFrame", () =>
        {
            if (Selected is { } row) openFrame(row.CandidateFrameIndex);
        });
        var close = new Button { Content = "Close", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 6) };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        Grid.SetRow(actions, 3); root.Children.Add(actions);
        if (_all.Count > 0) _grid.SelectedIndex = 0;
    }
}
