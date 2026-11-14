using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using RbgCanInsight.App.Common;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.App;

/// <summary>Runs <see cref="CatalogConsistencyChecker"/> over the loaded CDD/ODX/PDX catalogs, the
/// diagnostic counterpart of the DBC consistency tab.</summary>
public partial class CatalogCheckWindow : Window
{
    private readonly Func<IReadOnlyList<DidCatalog>> _catalogs;
    private CatalogConsistencyReport? _report;

    public CatalogCheckWindow(Func<IReadOnlyList<DidCatalog>> catalogs)
    {
        _catalogs = catalogs;
        InitializeComponent();
        TitleBarTheme.Hook(this);
        Loaded += (_, _) => Run();
    }

    private void OnRunClick(object sender, RoutedEventArgs e) => Run();

    private void Run()
    {
        try
        {
            var catalogs = _catalogs();
            if (catalogs.Count == 0)
            {
                SummaryText.Text = "No CDD, ODX or PDX file is loaded. Load a diagnostic description first.";
                FindingsGrid.ItemsSource = null;
                ExportButton.IsEnabled = false;
                return;
            }

            _report = CatalogConsistencyChecker.Check(catalogs);
            FindingsGrid.ItemsSource = _report.Findings;
            var text = _report.ToSummaryText();
            SummaryText.Text = text[..text.IndexOf('\n')].Trim();
            ExportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Catalog check failed: " + ex.Message;
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        try
        {
            var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "catalog_consistency.csv" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, _report.ToCsv(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Export failed: " + ex.Message;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
