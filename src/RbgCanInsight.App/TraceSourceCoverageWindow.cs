using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using RbgCanInsight.App.Common;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.App;

/// <summary>Source-aware coverage matrix; never modifies imported frames or databases.</summary>
public sealed class TraceSourceCoverageWindow : Window
{
    private readonly TraceSourceCoverageReport _report;

    public TraceSourceCoverageWindow(TraceSourceCoverageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
        Title = "Source comparison — CAN ID coverage";
        Width = 1250;
        Height = 720;
        MinWidth = 750;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        TitleBarTheme.Hook(this);

        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var title = new TextBlock
        {
            Text = "Compare the same original CAN channel and identifier across files",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5),
            TextWrapping = TextWrapping.Wrap
        };
        top.Children.Add(title);
        var summary = new TextBlock { Text = report.Summary, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6) };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        AutomationProperties.SetAutomationId(summary, "trace.coverage.summary");
        top.Children.Add(summary);
        var warning = new TextBlock
        {
            Text = "Observational coverage only. Absent IDs do not establish a defect: recordings may have different " +
                   "duration, network connections or time bases. Selected merge mode: " + report.MergeMode + ".",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        warning.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        top.Children.Add(warning);

        var search = new TextBox { Width = 330, HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Filter the coverage table by CAN ID, channel or coverage status" };
        AutomationProperties.SetAutomationId(search, "trace.coverage.search");
        AutomationProperties.SetName(search, "Search cross-source CAN ID coverage");
        top.Children.Add(search);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true,
            CanUserSortColumns = true,
            EnableRowVirtualization = true,
            SelectionMode = DataGridSelectionMode.Single,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12
        };
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        AutomationProperties.SetAutomationId(grid, "trace.coverage.grid");
        AddColumn(grid, "Ch", nameof(TraceSourceCoverageRow.ChannelText), 60);
        AddColumn(grid, "CAN ID", nameof(TraceSourceCoverageRow.IdText), 120);
        AddColumn(grid, "Presence", nameof(TraceSourceCoverageRow.PresenceText), 95);
        AddColumn(grid, "Status", nameof(TraceSourceCoverageRow.Finding), 125);
        AddColumn(grid, "Total", nameof(TraceSourceCoverageRow.TotalFrames), 95);
        AddColumn(grid, "First [s]", nameof(TraceSourceCoverageRow.FirstSeconds), 118);
        AddColumn(grid, "Last [s]", nameof(TraceSourceCoverageRow.LastSeconds), 118);

        for (var i = 0; i < report.SourcePaths.Count; i++)
        {
            var sourceName = Path.GetFileName(report.SourcePaths[i]);
            // Parent directory disambiguates common filenames like trace.asc.
            if (report.SourcePaths.Count(p => string.Equals(Path.GetFileName(p),
                sourceName, StringComparison.OrdinalIgnoreCase)) > 1)
                sourceName += " · " + Path.GetFileName(Path.GetDirectoryName(report.SourcePaths[i]));
            AddColumn(grid, sourceName, $"Counts[{i}]", 125);
        }

        var view = CollectionViewSource.GetDefaultView(report.Rows);
        grid.ItemsSource = view;
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            view.Filter = item => item is TraceSourceCoverageRow row &&
                (query.Length == 0 ||
                 row.IdText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 row.ChannelText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 row.Finding.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 row.PresenceText.Contains(query, StringComparison.OrdinalIgnoreCase));
        };

        var footer = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var export = new Button { Content = "Export coverage CSV…", Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(export, "trace.coverage.export");
        export.Click += (_, _) => ExportCsv();
        var close = new Button { Content = "Close", Padding = new Thickness(14, 5, 14, 5) };
        close.Click += (_, _) => Close();
        footer.Children.Add(export);
        footer.Children.Add(close);
        root.Children.Add(footer);
        root.Children.Add(grid);
        Content = root;
    }

    private static void AddColumn(DataGrid grid, string name, string binding, double width) =>
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = name,
            Binding = new Binding(binding) { Mode = BindingMode.OneWay },
            Width = new DataGridLength(width),
            MinWidth = Math.Min(width, 55)
        });

    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export cross-file CAN coverage",
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName = "rbg-source-coverage.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", new[]
            {
                "Original channel", "Identifier", "Extended", "Sources present", "Total frames", "First timestamp ns", "Last timestamp ns"
            }.Concat(_report.SourcePaths).Select(Escape)));
            foreach (var row in _report.Rows)
            {
                var values = new[]
                {
                    (row.OriginalChannel + 1).ToString(CultureInfo.InvariantCulture),
                    row.IdText,
                    row.Extended ? "true" : "false",
                    row.PresenceText,
                    row.TotalFrames.ToString(CultureInfo.InvariantCulture),
                    row.FirstTimestampNanoseconds.ToString(CultureInfo.InvariantCulture),
                    row.LastTimestampNanoseconds.ToString(CultureInfo.InvariantCulture)
                }.Concat(row.Counts.Select(c => c.ToString(CultureInfo.InvariantCulture)));
                csv.AppendLine(string.Join(",", values.Select(Escape)));
            }
            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Coverage export failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
