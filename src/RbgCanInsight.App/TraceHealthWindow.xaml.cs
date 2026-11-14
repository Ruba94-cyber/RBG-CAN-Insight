using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

public partial class TraceHealthWindow : Window
{
    private readonly MainViewModel _viewModel;

    public TraceHealthWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        TitleBarTheme.Hook(this);
        DataContext = viewModel;
    }

    private void OnOpenLargestGapClick(object sender, RoutedEventArgs e) =>
        OpenSelectedRow();

    private void OnHealthRowDoubleClick(object sender, MouseButtonEventArgs e) =>
        OpenSelectedRow();

    private void OpenSelectedRow()
    {
        if (HealthGrid.SelectedItem is TraceHealthRowViewModel row)
        {
            _viewModel.OpenTraceHealthRow(row);
            return;
        }

        _viewModel.OpenTraceHealthRow(null);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
