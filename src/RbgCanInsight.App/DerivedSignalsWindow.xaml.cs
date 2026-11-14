using System.IO;
using System.Windows;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

/// <summary>Builds offline derived signals from recorded samples with the safe expression engine.</summary>
public partial class DerivedSignalsWindow : Window
{
    private readonly DerivedSignalsViewModel _viewModel;

    public DerivedSignalsWindow(DerivedSignalsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        TitleBarTheme.Hook(this);
        DataContext = viewModel;
    }

    /// <summary>Opens the window over <paramref name="owner"/> using the recorded signals of the loaded trace.</summary>
    public static void Open(Window owner, MainViewModel vm)
    {
        var window = new DerivedSignalsWindow(new DerivedSignalsViewModel(vm.Signals.AllSeries)) { Owner = owner, _main = vm };
        window.Show();
    }

    private void OnOpenQuickPlot(object sender, RoutedEventArgs e)
    {
        var series = _viewModel.CreatePlotSeries(out var reason);
        if (series is null)
        {
            MessageBox.Show(this, reason, "Derived signals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new SignalDetailWindow(series) { Owner = this }.Show();
    }

    private MainViewModel _main = null!;

    private void OnAddToMainChart(object sender, RoutedEventArgs e)
    {
        var timeline = _viewModel.CreateTimeline(out var reason);
        if (timeline is null)
        {
            MessageBox.Show(this, reason, "Derived signals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _main.Signals.AddDerivedSignal(timeline);
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasResult)
        {
            MessageBox.Show(this, "Press Preview first.", "Derived signals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export derived signal",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "derived-signal.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _viewModel.BuildCsv(), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
