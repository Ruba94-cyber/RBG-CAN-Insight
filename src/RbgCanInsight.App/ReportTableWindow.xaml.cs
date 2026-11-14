using RbgCanInsight.App.Common;
using System.Data;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace RbgCanInsight.App;

/// <summary>Read-only evidence table in its own window, exportable as CSV.</summary>
public partial class ReportTableWindow : Window
{
    private readonly DataTable _table;

    public ReportTableWindow(string title, string note, DataTable table)
    {
        InitializeComponent();
        RbgCanInsight.App.Common.TitleBarTheme.Hook(this);
        Title = title;
        _table = table;
        ReportGrid.ItemsSource = table.DefaultView;
        Summary.Text = table.Rows.Count == 0
            ? $"{note} - nothing found in the loaded trace."
            : $"{note} - {table.Rows.Count:N0} row(s).";
        if (table.Rows.Count == 0)
        {
            EmptyMessage.Text = $"Nothing to show: {note.TrimEnd('.')} found no matching data in the loaded trace.";
            EmptyMessage.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Builds explicit text columns so that every cell's text is also its accessible name
    /// (the default cell name is the row object's type name, which assistive technology reads aloud).
    /// </summary>
    private void OnAutoGeneratingColumn(object sender, System.Windows.Controls.DataGridAutoGeneratingColumnEventArgs e)
    {
        var header = e.PropertyName;
        var textStyle = new Style(typeof(System.Windows.Controls.TextBlock));
        textStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty,
            new System.Windows.Data.Binding("Text") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self) }));
        textStyle.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextTrimmingProperty, System.Windows.TextTrimming.CharacterEllipsis));
        e.Column = new System.Windows.Controls.DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding($"[{header}]"),
            ElementStyle = textStyle,
            Width = new System.Windows.Controls.DataGridLength(1, System.Windows.Controls.DataGridLengthUnitType.Auto)
        };
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = Title.Replace(' ', '_') + ".csv" };
        if (dialog.ShowDialog(this) != true)
            return;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", _table.Columns.Cast<DataColumn>().Select(c => Escape(c.ColumnName))));
        foreach (DataRow row in _table.Rows)
            sb.AppendLine(string.Join(",", row.ItemArray.Select(v => Escape(Convert.ToString(v) ?? ""))));
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Escape(string text)
    {
        // Neutralise spreadsheet formula injection from trace-derived text.
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@')
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
