using System.Windows;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

public partial class DbcMergeWindow : Window
{
    public DbcMergeWindow(DbcMergeViewModel? viewModel = null)
    {
        InitializeComponent();
        TitleBarTheme.Hook(this);
        DataContext = viewModel ?? new DbcMergeViewModel();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
