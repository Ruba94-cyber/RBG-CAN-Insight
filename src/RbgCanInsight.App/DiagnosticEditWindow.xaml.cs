using System.Windows;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

public partial class DiagnosticEditWindow : Window
{
    public DiagnosticEditWindow(DiagnosticEditViewModel viewModel)
    {
        InitializeComponent();
        TitleBarTheme.Hook(this);
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
