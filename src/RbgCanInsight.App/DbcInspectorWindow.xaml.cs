using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.Dbc;

namespace RbgCanInsight.App;

/// <summary>Large detached DBC signal inspector sharing the main view model.</summary>
public partial class DbcInspectorWindow : Window
{
    private readonly MainWindow _main;
    private DbcConsistencyReport? _consistency;

    public DbcInspectorWindow(MainWindow main, MainViewModel viewModel)
    {
        _main = main;
        InitializeComponent();
        RbgCanInsight.App.Common.TitleBarTheme.Hook(this);
        DataContext = viewModel;
    }

    private void OnQuickPlotClick(object sender, RoutedEventArgs e) => _main.OpenDbcQuickPlot();

    private void OnRunConsistencyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var viewModel = (MainViewModel)DataContext;
            var row = viewModel.SelectedDbcDatabase;
            var database = row is null ? null : viewModel.Databases.FirstOrDefault(item => item.Name == row.Name);
            if (database is null)
            {
                ConsistencySummary.Text = "No DBC is selected. Load a DBC and select it first.";
                return;
            }

            // The source text enables the node / attribute / VAL_ rules; without a readable file the
            // model-only rules still run and the summary lists the skipped ones.
            string? sourceText = null;
            if (row is not null && !string.IsNullOrWhiteSpace(row.Path) && File.Exists(row.Path))
                sourceText = File.ReadAllText(row.Path);
            _consistency = DbcConsistencyChecker.Check(database, new DbcConsistencyOptions { SourceText = sourceText });
            ConsistencyGrid.ItemsSource = _consistency.Findings;
            var summary = _consistency.ToSummaryText();
            ConsistencySummary.Text = summary[..summary.IndexOf('\n')].Trim();
            ConsistencyExportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ConsistencySummary.Text = "Consistency check failed: " + ex.Message;
        }
    }

    private DbcNodeView? _nodeView;

    private sealed record NodeRow(string Name, string Comment, string TxText, string RxText);

    private static string Names(IEnumerable<DbcNodeMessageRef> refs) =>
        string.Join(", ", refs.Take(40).Select(r => r.Message)) + (refs.Count() > 40 ? $" … (+{refs.Count() - 40})" : "");

    private void OnReadNodesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var viewModel = (MainViewModel)DataContext;
            var row = viewModel.SelectedDbcDatabase;
            var database = row is null ? null : viewModel.Databases.FirstOrDefault(item => item.Name == row.Name);
            if (database is null || string.IsNullOrWhiteSpace(row?.Path) || !File.Exists(row.Path))
            {
                NodesSummary.Text = "Select a DBC that is loaded from a readable file first.";
                return;
            }
            var network = DbcNetworkDefinition.Read(File.ReadAllText(row.Path));
            _nodeView = new DbcNodeView(database, network);
            NodesGrid.ItemsSource = _nodeView.Nodes.Select(n => new NodeRow(
                n.Name + (n.Declared ? "" : " (not in BU_)"), n.Comment,
                $"{n.Transmits.Count}: {Names(n.Transmits)}", $"{n.Receives.Count}: {Names(n.Receives)}")).ToList();
            AttrDefGrid.ItemsSource = network.AttributeDefinitions;
            AttrValueGrid.ItemsSource = network.AttributeAssignments;
            NodeMatrixExportButton.IsEnabled = true;
            NodesSummary.Text = $"{network.Nodes.Count} BU_ node(s), {network.ExtraTransmitters.Count} BO_TX_BU_ statement(s), " +
                $"{network.AttributeDefinitions.Count} attribute definition(s), {network.AttributeAssignments.Count} value(s), " +
                $"{network.AttributeAssignments.Count(a => a.Problem is not null)} with a problem" +
                (network.Unmodelled.Count > 0 ? $"; {network.Unmodelled.Count} relational attribute statement(s) not modelled" : "") + ".";
        }
        catch (Exception ex)
        {
            NodesSummary.Text = "Reading nodes failed: " + ex.Message;
        }
    }

    private void OnExportMatrixClick(object sender, RoutedEventArgs e)
    {
        if (_nodeView is null) return;
        try
        {
            var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "dbc_network_matrix.csv" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, _nodeView.ToMatrixCsv(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            NodesSummary.Text = "Export failed: " + ex.Message;
        }
    }

    private void OnExportConsistencyClick(object sender, RoutedEventArgs e)
    {
        if (_consistency is null) return;
        try
        {
            var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "dbc_consistency.csv" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, _consistency.ToCsv(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            ConsistencySummary.Text = "Export failed: " + ex.Message;
        }
    }
}
