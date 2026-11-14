using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.Controls;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App.Views;

/// <summary>
/// Read-only investigation of one recorded DTC lifecycle event. The left table
/// points to original CAN frame pairs, the right plot reuses the decoded source
/// timelines. No ECU command or DTC clearing is possible in this window.
/// </summary>
public sealed class FaultContextWindow : Window
{
    private readonly IReadOnlyList<NearbySignalCandidateRow> _rows;
    private readonly DataGrid _changeGrid;

    public FaultContextWindow(
        Window owner,
        FaultEventContext report,
        IReadOnlyList<NearbySignalCandidateRow> rows,
        IReadOnlyList<DataStudioSnapshotCanRow> snapshotLinks,
        Func<bool> evidenceIsCurrent,
        Action<PlotSeriesViewModel> addToMainPlot,
        Action<int> openOriginalFrame,
        Action<DataStudioSnapshotCanRow> inspectDidCanLead,
        Action<DataStudioSnapshotCanRow> addDidCanLeadToPlot)
    {
        Owner = owner;
        Icon = owner.Icon;
        Title = $"Fault context — {report.Fault.CodeText} · {report.Fault.Ecu}";
        Width = Math.Min(1220, SystemParameters.WorkArea.Width - 36);
        Height = Math.Min(785, SystemParameters.WorkArea.Height - 36);
        MinWidth = 560;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        TitleBarTheme.Hook(this);
        _rows = rows;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void At(UIElement child, int row)
        {
            Grid.SetRow(child, row);
            root.Children.Add(child);
        }

        var heading = new TextBlock
        {
            Text = $"Recorded {report.Fault.CodeText} — {report.Fault.Name} · ECU {report.Fault.Ecu}" +
                $" · {report.Fault.SourceText} · {report.Fault.ChannelText}" +
                $" · event t={report.Event.TimestampNanoseconds / 1e9:F6}s",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 7)
        };
        At(heading, 0);

        var eventEvidence = new TextBlock
        {
            Text = $"{report.Event.Operation} · {report.Event.Outcome} · " +
                $"original DTC frame(s) #{string.Join(", #", report.Event.FrameIndices.Select(index => index + 1))}" +
                Environment.NewLine + report.EventMeaning + Environment.NewLine + report.StatusEvidence,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9),
            MaxHeight = 116
        };
        var eventScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = eventEvidence
        };
        At(eventScroll, 1);

        var declaredConditions = report.Fault.DefinitionAttributes.Count == 0
            ? ""
            : " · OEM DTC table reference (not recorded measurements): " +
              string.Join("; ", report.Fault.DefinitionAttributes
                  .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                  .Select(pair => pair.Key + ": " + pair.Value));
        var definition = report.Fault.DefinitionText;
        var catalogText = "Definition: " + definition + declaredConditions +
            (string.IsNullOrWhiteSpace(report.Fault.Description)
                ? ""
                : " · DTC description: " + report.Fault.Description);
        var recordText = catalogText + Environment.NewLine +
            (report.Event.Records.Count == 0
            ? "No CDD/ODX snapshot or extended-data was recorded for this selected event."
            : string.Join(Environment.NewLine, report.Event.Records.Select(record =>
                $"{record.SummaryText} · {record.SourceEvidenceText}" +
                (record.Values.Count > 0
                    ? " · decoded fields: " + string.Join("; ", record.Values.Select(v =>
                        v.Name + "=" + (string.IsNullOrWhiteSpace(v.Physical) ? v.Raw : v.Physical) +
                        (v.HasVerifiedSource ? " [CAN byte source verified]" : " [source unverified]")))
                    : ""))));
        var snapshot = new TextBlock
        {
            Text = recordText,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = recordText,
            Margin = new Thickness(0, 0, 0, 8),
            MaxHeight = 68
        };
        At(new ScrollViewer { Content = snapshot, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 2);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 185 });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star), MinWidth = 210 });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var leftLabel = new TextBlock
        {
            Text = $"Same-logger evidence · recorded changes ({rows.Count}) / snapshot DID→CAN leads ({snapshotLinks.Count}) · within " +
                $"{(report.Event.TimestampNanoseconds - report.WindowStartNanoseconds) / 1e9:G4}s before / " +
                $"{(report.WindowEndNanoseconds - report.Event.TimestampNanoseconds) / 1e9:G4}s after",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 6, 6)
        };
        Grid.SetColumn(leftLabel, 0); Grid.SetRow(leftLabel, 0); split.Children.Add(leftLabel);
        var rightLabel = new TextBlock
        {
            Text = "Actual CAN trends · top 5 label/time candidates · click X1, Shift-click X2 (sample-and-hold)",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetColumn(rightLabel, 2); Grid.SetRow(rightLabel, 0); split.Children.Add(rightLabel);

        _changeGrid = new DataGrid
        {
            ItemsSource = rows,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            AutoGenerateColumns = false,
            CanUserSortColumns = true,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            FontSize = 11.5
        };
        _changeGrid.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "faultContext.changesGrid");
        static DataGridTextColumn Column(string label, string property, double width) =>
            new() { Header = label, Binding = new Binding(property), Width = new DataGridLength(width) };
        _changeGrid.Columns.Add(Column("Signal", nameof(NearbySignalCandidateRow.Signal), 160));
        _changeGrid.Columns.Add(Column("Why suggested (NOT cause)", nameof(NearbySignalCandidateRow.CatalogMatchHint), 250));
        _changeGrid.Columns.Add(Column("Original source", nameof(NearbySignalCandidateRow.Source), 190));
        _changeGrid.Columns.Add(Column("Offset [s]", nameof(NearbySignalCandidateRow.RelativeTime), 100));
        _changeGrid.Columns.Add(Column("Time [s]", nameof(NearbySignalCandidateRow.TransitionTime), 105));
        _changeGrid.Columns.Add(Column("Sample gap", nameof(NearbySignalCandidateRow.SampleGap), 150));
        _changeGrid.Columns.Add(Column("Before → after", nameof(NearbySignalCandidateRow.Values), 145));
        _changeGrid.Columns.Add(Column("Original frames", nameof(NearbySignalCandidateRow.OriginalFrames), 125));
        var leadsGrid = new DataGrid
        {
            ItemsSource = snapshotLinks,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            AutoGenerateColumns = false,
            CanUserSortColumns = true,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            FontSize = 11.5
        };
        leadsGrid.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "faultContext.snapshotLeadsGrid");
        leadsGrid.Columns.Add(Column("Snapshot DID", nameof(DataStudioSnapshotCanRow.Did), 88));
        leadsGrid.Columns.Add(Column("Decoded field", nameof(DataStudioSnapshotCanRow.RecordedField), 175));
        leadsGrid.Columns.Add(Column("Snapshot value", nameof(DataStudioSnapshotCanRow.SnapshotValue), 112));
        leadsGrid.Columns.Add(Column("CAN / DBC", nameof(DataStudioSnapshotCanRow.CanSignal), 185));
        leadsGrid.Columns.Add(Column("Source", nameof(DataStudioSnapshotCanRow.CanSource), 205));
        leadsGrid.Columns.Add(Column("Separate value pairs", nameof(DataStudioSnapshotCanRow.Pairs), 115));
        leadsGrid.Columns.Add(Column("Verdict", nameof(DataStudioSnapshotCanRow.Verdict), 110));
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem
        {
            Header = $"Observed changes ({rows.Count})",
            Content = _changeGrid
        });
        tabs.Items.Add(new TabItem
        {
            Header = $"Snapshot DID ↔ CAN leads ({snapshotLinks.Count})",
            Content = leadsGrid,
            ToolTip = "Candidates from separately paired original DID and CAN samples. Snapshot-to-CAN signal identity is NOT verified."
        });
        Grid.SetColumn(tabs, 0); Grid.SetRow(tabs, 1); split.Children.Add(tabs);

        var splitter = new GridSplitter
        {
            Width = 7,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        Grid.SetColumn(splitter, 1); Grid.SetRow(splitter, 1); split.Children.Add(splitter);

        var plotted = rows.Take(5).Select(candidate => candidate.Series).ToArray();
        var chart = new SignalPlotControl
        {
            ItemsSource = plotted,
            IgnorePlottedFilter = true,
            YAxisModeIndex = 0,
            TimeWindowSeconds = (report.WindowEndNanoseconds - report.WindowStartNanoseconds) / 1e9,
            WindowEndTimestampNanoseconds = report.WindowEndNanoseconds,
            GlobalStartTimestampNanoseconds = report.WindowStartNanoseconds,
            GlobalEndTimestampNanoseconds = report.WindowEndNanoseconds
        };
        chart.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "faultContext.chart");
        chart.Loaded += (_, _) => chart.SetViewRange(
            report.WindowStartNanoseconds, report.WindowEndNanoseconds);
        var chartBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = chart
        };
        chartBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetColumn(chartBorder, 2); Grid.SetRow(chartBorder, 1); split.Children.Add(chartBorder);
        At(split, 3);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var actions = new WrapPanel();
        var addSelected = new Button { Content = "＋ Add selected to main plot", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 6), IsEnabled = false };
        addSelected.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "faultContext.addSelected");
        _changeGrid.SelectionChanged += (_, _) => addSelected.IsEnabled = _changeGrid.SelectedItem is NearbySignalCandidateRow;
        addSelected.Click += (_, _) =>
        {
            if (!evidenceIsCurrent()) { ShowStale(); return; }
            if (_changeGrid.SelectedItem is NearbySignalCandidateRow candidate)
                addToMainPlot(candidate.Series);
        };
        actions.Children.Add(addSelected);

        var addTop = new Button { Content = "＋ Add first 5 to main plot", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 6), IsEnabled = rows.Count > 0 };
        addTop.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "faultContext.addTopFive");
        addTop.Click += (_, _) =>
        {
            if (!evidenceIsCurrent()) { ShowStale(); return; }
            foreach (var row in _rows.Take(5)) addToMainPlot(row.Series);
        };
        actions.Children.Add(addTop);

        var openChangeFrame = new Button { Content = "Open selected CAN frame", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 6), IsEnabled = false };
        openChangeFrame.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "faultContext.openChangeFrame");
        _changeGrid.SelectionChanged += (_, _) => openChangeFrame.IsEnabled = _changeGrid.SelectedItem is NearbySignalCandidateRow;
        openChangeFrame.Click += (_, _) =>
        {
            if (!evidenceIsCurrent()) { ShowStale(); return; }
            if (_changeGrid.SelectedItem is NearbySignalCandidateRow row)
                openOriginalFrame(row.Evidence.After.FrameIndex);
        };
        actions.Children.Add(openChangeFrame);

        var openEventFrame = new Button { Content = "Open DTC source frame", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 6) };
        openEventFrame.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "faultContext.openEventFrame");
        openEventFrame.Click += (_, _) =>
        {
            if (!evidenceIsCurrent()) { ShowStale(); return; }
            openOriginalFrame(report.Event.FrameIndices[0]);
        };
        actions.Children.Add(openEventFrame);

        var inspectLink = new Button
        {
            Content = "Inspect selected DID↔CAN pairs",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 7, 6),
            IsEnabled = false
        };
        inspectLink.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "faultContext.inspectDidCan");
        leadsGrid.SelectionChanged += (_, _) =>
            inspectLink.IsEnabled = leadsGrid.SelectedItem is DataStudioSnapshotCanRow;
        inspectLink.Click += (_, _) =>
        {
            if (!evidenceIsCurrent()) { ShowStale(); return; }
            if (leadsGrid.SelectedItem is DataStudioSnapshotCanRow link)
                inspectDidCanLead(link);
        };
        actions.Children.Add(inspectLink);

        var addLink = new Button
        {
            Content = "＋ Add selected DID candidate",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 7, 6),
            IsEnabled = false
        };
        addLink.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
            "faultContext.addDidCan");
        leadsGrid.SelectionChanged += (_, _) =>
            addLink.IsEnabled = leadsGrid.SelectedItem is DataStudioSnapshotCanRow;
        addLink.Click += (_, _) =>
        {
            if (!evidenceIsCurrent()) { ShowStale(); return; }
            if (leadsGrid.SelectedItem is DataStudioSnapshotCanRow link)
                addDidCanLeadToPlot(link);
        };
        actions.Children.Add(addLink);

        var dismiss = new Button { Content = "Close", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 6) };
        dismiss.Click += (_, _) => Close();
        actions.Children.Add(dismiss);
        Grid.SetRow(actions, 0); footer.Children.Add(actions);

        var limitation = new TextBlock
        {
            Text = report.Limitations,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5,
            Foreground = Brushes.Gray,
            MaxHeight = 50
        };
        Grid.SetRow(limitation, 1); footer.Children.Add(limitation);
        At(footer, 4);

        Content = root;
        if (rows.Count > 0) _changeGrid.SelectedIndex = 0;

        void ShowStale()
        {
            MessageBox.Show(this,
                "The trace, diagnostic history or DBC catalog changed. Reopen this investigation using the current recorded event.",
                "Original evidence changed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
