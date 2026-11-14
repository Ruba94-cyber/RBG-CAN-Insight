using System.Windows;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

/// <summary>Shows one decoded signal from the base trace and the comparison trace overlaid on a
/// shared relative-time axis, so a "value changed" or "activity changed" row in the Statistics
/// compare tab can be read as a shape, not just two numbers.</summary>
public partial class CompareDiffWindow : Window
{
    public CompareDiffWindow(CompareSignalRow row)
    {
        InitializeComponent();
        TitleBarTheme.Hook(this);
        var label = string.IsNullOrEmpty(row.Signal) ? row.Message : $"{row.Message}.{row.Signal}";
        TitleText.Text = label;
        SubtitleText.Text = $"{row.Ecu} · base {row.BaseSamples:N0} samples ({row.BaseLast}) · compare {row.CompareSamples:N0} samples ({row.CompareLast}) · {row.Status}";
        Chart.BaseReport = row.BaseReport;
        Chart.CompareReport = row.CompareReport;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
