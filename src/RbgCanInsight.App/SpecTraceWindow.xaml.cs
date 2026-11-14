using System.Windows;
using System.Windows.Input;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

public partial class SpecTraceWindow : Window
{
    private readonly SpecTraceViewModel _viewModel;

    public SpecTraceWindow(MainViewModel main)
    {
        _viewModel = new SpecTraceViewModel(main);
        InitializeComponent();
        TitleBarTheme.Hook(this);
        DataContext = _viewModel;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => _viewModel.OpenSelectedFrame();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
