using System.Windows;
using System.Windows.Input;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

public partial class SignalOverlayWindow : Window
{
    private readonly SignalOverlayViewModel _viewModel;

    public SignalOverlayWindow(MainViewModel main, SignalOverlayContext context)
    {
        _viewModel = new SignalOverlayViewModel(main, context);
        InitializeComponent();
        TitleBarTheme.Hook(this);
        DataContext = _viewModel;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => _viewModel.OpenSelectedFrame();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
