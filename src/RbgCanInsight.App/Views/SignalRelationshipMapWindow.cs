using RbgCanInsight.Domain;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.Controls;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App.Views;

/// <summary>
/// Compact, discoverable visual index of explicitly observed pairwise CAN
/// signal associations. Graph lines are real source-qualified analytical
/// leads, not ECU wiring or inferred causal dependencies.
/// </summary>
public sealed class SignalRelationshipMapWindow : Window
{
    private readonly ListBox _groups;
    private readonly DataGrid _links;
    private readonly Canvas _map;
    private readonly SignalPlotControl _preview;
    private readonly TextBlock _selectedDetail;
    private readonly TextBlock _mapMessage;
    private readonly SignalRelationshipMapReport _report;
    private readonly IReadOnlyDictionary<string, PlotSeriesViewModel> _sourceSeries;
    private readonly Func<bool> _evidenceCurrent;
    private readonly Action<PlotSeriesViewModel> _addPlot;
    private readonly Action<int> _openFrame;
    private readonly Func<int, int> _originalSourceAt;
    private bool _isInspectingChanges;

    private SignalMapGroup? CurrentGroup => _groups.SelectedItem as SignalMapGroup;
    private SignalMapEdge? CurrentLink => _links.SelectedItem as SignalMapEdge;

    public SignalRelationshipMapWindow(
        Window owner,
        SignalRelationshipMapReport report,
        IReadOnlyDictionary<string, PlotSeriesViewModel> sourceSeries,
        Func<bool> evidenceCurrent,
        Action<PlotSeriesViewModel> addPlot,
        Action<int> openFrame,
        Func<int, int> originalSourceAt)
    {
        Owner = owner;
        Icon = owner.Icon;
        Title = $"CAN signal relationship map — Logger {report.OriginalSourceIndex + 1}";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(1160, SystemParameters.WorkArea.Width - 28);
        Height = Math.Min(790, SystemParameters.WorkArea.Height - 28);
        MinWidth = 640;
        MinHeight = 430;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        TitleBarTheme.Hook(this);

        _report = report;
        _sourceSeries = sourceSeries;
        _evidenceCurrent = evidenceCurrent;
        _addPlot = addPlot;
        _openFrame = openFrame;
        _originalSourceAt = originalSourceAt;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = "Discover which recorded signals move together",
            FontSize = 18, FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = report.Summary + Environment.NewLine +
                "Choose a group, click a connected signal, then inspect each observed link and its two original curves. " +
                "A line shows a statistical/temporal association, NOT a physical connection, controlling influence or proven cause." +
                Environment.NewLine + EvidenceKinds.Legend,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brushes.Gray
        });
        Grid.SetRow(header, 0); root.Children.Add(header);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245), MinWidth = 185 });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 330 });
        Grid.SetRow(split, 1); root.Children.Add(split);

        var left = new DockPanel { LastChildFill = true };
        var leftHeader = new StackPanel { Margin = new Thickness(0, 0, 6, 8) };
        leftHeader.Children.Add(new TextBlock
        {
            Text = $"Detected groups ({report.Groups.Count})",
            FontWeight = FontWeights.SemiBold
        });
        leftHeader.Children.Add(new TextBlock
        {
            Text = "Grouped by observed links; isolated signals without sufficient evidence are not labeled unrelated.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Foreground = Brushes.Gray
        });
        var searchGroups = new TextBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "Filter groups by any source-exact CAN signal, message name, original wire CAN ID or DBC database."
        };
        AutomationProperties.SetAutomationId(searchGroups, "relationMap.searchGroups");
        leftHeader.Children.Add(searchGroups);
        DockPanel.SetDock(leftHeader, Dock.Top);
        left.Children.Add(leftHeader);
        _groups = new ListBox
        {
            ItemsSource = report.Groups,
            DisplayMemberPath = nameof(SignalMapGroup.Title),
            SelectionMode = SelectionMode.Single
        };
        AutomationProperties.SetAutomationId(_groups, "relationMap.groups");
        searchGroups.TextChanged += (_, _) =>
        {
            var query = searchGroups.Text.Trim();
            var previous = CurrentGroup;
            var matching = query.Length == 0
                ? report.Groups
                : report.Groups.Where(group => group.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    group.Members.Any(member =>
                        member.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        member.Signal.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        member.Message.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        member.DatabaseName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        $"0x{member.SourceIdentifier:X}".Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            _groups.ItemsSource = matching;
            _groups.SelectedItem = previous is not null && matching.Contains(previous)
                ? previous : matching.FirstOrDefault();
        };
        left.Children.Add(_groups);
        Grid.SetColumn(left, 0); split.Children.Add(left);

        var separator = new GridSplitter
        {
            Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        Grid.SetColumn(separator, 1); split.Children.Add(separator);

        var right = new Grid { Margin = new Thickness(6, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(160) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 85 });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(155), MinHeight = 110 });
        _mapMessage = new TextBlock
        {
            Text = "Select a group to inspect its explicitly observed links.",
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "Groups are network components of evidence links; they are not inferred direct relationships among every pair."
        };
        AutomationProperties.SetAutomationId(_mapMessage, "relationMap.graph");
        AutomationProperties.SetName(_mapMessage,
            "Source-qualified visual relationship graph and selected group");
        Grid.SetRow(_mapMessage, 0); right.Children.Add(_mapMessage);

        var mapScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _map = new Canvas { Width = 600, Height = 150, ClipToBounds = true };
        // The accessible graph label above is a TextBlock; node buttons
        // remain individually reachable by keyboard and screen reader.
        mapScroll.Content = _map;
        Grid.SetRow(mapScroll, 1); right.Children.Add(mapScroll);

        _links = new DataGrid
        {
            ItemsSource = Array.Empty<SignalMapEdge>(),
            IsReadOnly = true,
            CanUserSortColumns = true,
            AutoGenerateColumns = false,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            SelectionMode = DataGridSelectionMode.Single,
            FontSize = 11.5
        };
        AutomationProperties.SetAutomationId(_links, "relationMap.links");
        static DataGridTextColumn Col(string title, string field, int width) =>
            new() { Header = title, Binding = new Binding(field), Width = new DataGridLength(width) };
        _links.Columns.Add(Col("Reference", nameof(SignalMapEdge.ALabel), 162));
        _links.Columns.Add(Col("Related", nameof(SignalMapEdge.BLabel), 162));
        _links.Columns.Add(Col("Evidence kind", nameof(SignalMapEdge.TypeText), 230));
        _links.Columns.Add(Col("Engineering check", nameof(SignalMapEdge.InvestigationHint), 300));
        _links.Columns.Add(Col("Strength", nameof(SignalMapEdge.StrengthText), 155));
        _links.Columns.Add(Col("Timing", nameof(SignalMapEdge.OrderText), 265));
        Grid.SetRow(_links, 2); right.Children.Add(_links);

        var previewGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        _preview = new SignalPlotControl
        {
            IgnorePlottedFilter = true,
            YAxisModeIndex = 2,
            ShowInlineLegend = true,
            LinearInterpolation = false
        };
        AutomationProperties.SetAutomationId(_preview, "relationMap.plot");
        _preview.Loaded += (_, _) =>
        {
            if (CurrentLink is { } link)
                SetPreviewWindow(link);
        };
        var chartFrame = new Border { Child = _preview, CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1), ClipToBounds = true };
        chartFrame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetColumn(chartFrame, 0); previewGrid.Children.Add(chartFrame);
        _selectedDetail = new TextBlock
        {
            Text = "Choose a link to inspect exact paired source-frame evidence.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var detailScroll = new ScrollViewer
        {
            Content = _selectedDetail,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetColumn(detailScroll, 1); previewGrid.Children.Add(detailScroll);
        Grid.SetRow(previewGrid, 3); right.Children.Add(previewGrid);
        Grid.SetColumn(right, 2); split.Children.Add(right);

        _groups.SelectionChanged += (_, _) => RefreshGroup();
        _links.SelectionChanged += (_, _) => RefreshLink();

        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button Action(string title, string id, Action click)
        {
            var button = new Button
            {
                Content = title, Margin = new Thickness(0, 0, 7, 5),
                Padding = new Thickness(10, 5, 10, 5)
            };
            AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) =>
            {
                if (!_evidenceCurrent())
                {
                    MessageBox.Show(this,
                        "The physical CAN recording or DBC data changed. Reopen the signal map.",
                        "Outdated CAN evidence", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                click();
            };
            footer.Children.Add(button);
            return button;
        }
        Action("＋ Add selected pair", "relationMap.addPair", () =>
        {
            if (CurrentLink is not { } link) return;
            Add(link.A);
            Add(link.B);
        });
        Action("＋ Add group (max 8)", "relationMap.addGroup", () =>
        {
            if (CurrentGroup is not { } group) return;
            foreach (var timeline in group.Members.Take(8)) Add(timeline);
        });
        Action("Open reference CAN frame", "relationMap.openRef", () =>
        {
            if (CurrentLink is { } link)
                _openFrame(link.Evidence.ReferenceFrameIndex);
        });
        Action("Open related CAN frame", "relationMap.openRelated", () =>
        {
            if (CurrentLink is { } link)
                _openFrame(link.Evidence.CandidateFrameIndex);
        });
        var inspect = new Button
        {
            Content = "Inspect observed changes…",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 7, 5)
        };
        AutomationProperties.SetAutomationId(inspect, "relationMap.inspectChanges");
        inspect.Click += OnInspectObservedChangesClick;
        footer.Children.Add(inspect);
        Action("Export observed evidence CSV…", "relationMap.exportCsv", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export CAN relationship observations (not causal proof)",
                FileName = $"can-signal-relations-logger-{_report.OriginalSourceIndex + 1}.csv",
                Filter = "Comma-separated evidence (*.csv)|*.csv",
                DefaultExt = ".csv",
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true) return;
            System.IO.File.WriteAllText(dialog.FileName,
                SignalRelationshipMapCsv.Export(_report),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        });
        var close = new Button { Content = "Close", Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 7, 5) };
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        Grid.SetRow(footer, 2); root.Children.Add(footer);

        if (report.Groups.Count > 0)
            _groups.SelectedIndex = 0;
    }

    private async void OnInspectObservedChangesClick(object sender, RoutedEventArgs e)
    {
        if (_isInspectingChanges || CurrentLink is not { } link) return;
        if (!_evidenceCurrent())
        {
            MessageBox.Show(this, "The original recording changed; run discovery again.",
                "Source changed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _isInspectingChanges = true;
        try
        {
            // The original frame-source callback and decoded samples were
            // snapshotted by MainWindow. Reject stale results before display.
            using var cancellation = new CancellationTokenSource();
            void CancelOnClosed(object? s, EventArgs args) => cancellation.Cancel();
            Closed += CancelOnClosed;
            try
            {
                var report = await Task.Run(() => SignalChangeWitnessFinder.Find(
                    link.A, link.B, _originalSourceAt,
                    token: cancellation.Token,
                    centerTimestampNanoseconds: link.Evidence.ReferenceTimestampNanoseconds),
                    cancellation.Token);
                if (!IsLoaded || !_evidenceCurrent()) return;
                new SignalChangeWitnessWindow(this, report,
                    _evidenceCurrent, _openFrame).Show();
            }
            finally
            {
                Closed -= CancelOnClosed;
            }
        }
        catch (OperationCanceledException)
        {
            // The view closed or source changed while inspecting changes.
        }
        catch (Exception ex)
        {
            if (IsLoaded) MessageBox.Show(this,
                "Could not inspect original CAN transitions: " + ex.Message,
                "Observed changes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isInspectingChanges = false;
        }
    }

    private void Add(SignalTimeline timeline)
    {
        if (_sourceSeries.TryGetValue(timeline.Key, out var series) &&
            ReferenceEquals(series.Timeline, timeline))
            _addPlot(series);
    }

    private void RefreshGroup()
    {
        _map.Children.Clear();
        if (CurrentGroup is not { } group)
        {
            _links.ItemsSource = Array.Empty<SignalMapEdge>();
            _selectedDetail.Text = "No connected group selected.";
            return;
        }
        _mapMessage.Text = group.Title + " · " + group.StartingPointText +
            (group.Members.Count > 8 ? $" · diagram shows 8 of {group.Members.Count} nodes" : "");
        _mapMessage.ToolTip = group.Warning + Environment.NewLine +
            group.SignalNames;
        _links.ItemsSource = group.Edges;
        var visible = group.Members
            .OrderByDescending(member => group.Edges.Count(edge =>
                ReferenceEquals(edge.A, member) || ReferenceEquals(edge.B, member)))
            .ThenBy(member => member.Key, StringComparer.Ordinal)
            .Take(8).ToArray();
        var nodeCenters = new Dictionary<string, Point>(StringComparer.Ordinal);
        for (var index = 0; index < visible.Length; index++)
        {
            var angle = 2 * Math.PI * index / visible.Length - Math.PI / 2;
            nodeCenters[visible[index].Key] = new Point(
                300 + 220 * Math.Cos(angle), 76 + 51 * Math.Sin(angle));
        }
        // Draw only explicitly validated pairwise edges, never complete the
        // graph transitively. Some large groups show an intentionally
        // incomplete node preview, while the table retains all edges.
        foreach (var edge in group.Edges)
        {
            if (!nodeCenters.TryGetValue(edge.A.Key, out var a) ||
                !nodeCenters.TryGetValue(edge.B.Key, out var b))
                continue;
            _map.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = Brushes.SlateGray,
                StrokeThickness = 1.6,
                Opacity = 0.7,
                IsHitTestVisible = false
            });
        }
        foreach (var timeline in visible)
        {
            var pt = nodeCenters[timeline.Key];
            var node = new Button
            {
                Content = timeline.Signal.Name.Length > 20
                    ? timeline.Signal.Name[..17] + "…" : timeline.Signal.Name,
                Width = 132, Height = 29, FontSize = 11,
                ToolTip = timeline.Key + Environment.NewLine +
                    "Choose this recorded signal to highlight one observed connection."
            };
            AutomationProperties.SetAutomationId(node, "relationMap.node");
            node.Click += (_, _) =>
            {
                var candidate = group.Edges.FirstOrDefault(edge =>
                    ReferenceEquals(edge.A, timeline) || ReferenceEquals(edge.B, timeline));
                if (candidate is not null) _links.SelectedItem = candidate;
            };
            Canvas.SetLeft(node, pt.X - node.Width / 2);
            Canvas.SetTop(node, pt.Y - node.Height / 2);
            _map.Children.Add(node);
        }
        _links.SelectedIndex = group.Edges.Count > 0 ? 0 : -1;
    }

    private void SetPreviewWindow(SignalMapEdge link)
    {
        var a = link.A.Samples;
        var b = link.B.Samples;
        if (a.Count == 0 || b.Count == 0) return;
        var from = Math.Max(a[0].TimestampNanoseconds, b[0].TimestampNanoseconds);
        var to = Math.Min(a[^1].TimestampNanoseconds, b[^1].TimestampNanoseconds);
        if (to <= from)
        {
            from = Math.Min(a[0].TimestampNanoseconds, b[0].TimestampNanoseconds);
            to = Math.Max(a[^1].TimestampNanoseconds, b[^1].TimestampNanoseconds);
        }
        if (to <= from) return;
        _preview.GlobalStartTimestampNanoseconds = from;
        _preview.GlobalEndTimestampNanoseconds = to;
        _preview.WindowEndTimestampNanoseconds = to;
        if (_preview.IsLoaded) _preview.SetViewRange(from, to);
    }

    private void RefreshLink()
    {
        if (CurrentLink is not { } link) return;
        if (!_sourceSeries.TryGetValue(link.A.Key, out var a) ||
            !_sourceSeries.TryGetValue(link.B.Key, out var b))
            return;
        _preview.ItemsSource = new[] { a, b };
        SetPreviewWindow(link);
        _selectedDetail.Text = link.InvestigationHint + Environment.NewLine +
            link.EvidenceText + Environment.NewLine +
            link.Evidence.OriginalFrames + Environment.NewLine +
            "A graph line means an observed two-signal comparison; " +
            "neighboring nodes are not necessarily related to each other.";
    }
}
