using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App.Views;

/// <summary>Small view-only glue for the guided offline Data Studio. It shares
/// the current MainViewModel; it never imports, copies or modifies recordings.</summary>
public partial class DataStudioView : UserControl
{
    public DataStudioView() => InitializeComponent();

    private void OnStudioLoaded(object sender, RoutedEventArgs e) => UpdateCompactLayout();
    private void OnStudioSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCompactLayout();

    /// <summary>Protect the original evidence table from being squeezed out
    /// by the optional summary panel at 1024×640 and high DPI.</summary>
    private void UpdateCompactLayout()
    {
        if (AggregateScroll is null || AggregateGrid is null || ActualHeight <= 0)
            return;
        AggregateScroll.MaxHeight = Math.Clamp(ActualHeight * 0.27, 125, 255);
        AggregateGrid.Height = Math.Clamp(ActualHeight * 0.17, 84, 155);
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel host) return;
        host.DataStudio.RunQueryCommand.Execute(null);
        e.Handled = true;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel host || host.Trace.SelectedRow is null) return;
        host.DataStudio.OpenSelectedFrameCommand.Execute(null);
        e.Handled = true;
    }

    private void OnInvestigateFaultContextClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is RbgCanInsight.App.MainWindow main)
        {
            var seconds = FaultContextWindowChoice.SelectedIndex switch
            {
                0 => 1L,
                2 => 30L,
                3 => 120L,
                _ => 5L
            };
            main.OpenFaultContextFromDataStudio(seconds * 1_000_000_000L);
        }
    }

    private async void OnCompareSourcesClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel host || !host.CanCompareTraceSources) return;
        var report = await host.BuildSourceCoverageAsync();
        if (report is null) return;
        new TraceSourceCoverageWindow(report) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
