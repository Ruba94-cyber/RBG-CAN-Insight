using System.Windows;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

public partial class QueryBuilderWindow : Window
{
    public QueryBuilderWindow(QueryBuilderViewModel viewModel)
    {
        InitializeComponent();
        TitleBarTheme.Hook(this);
        DataContext = viewModel;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        // The command applies the query; keep the window open so the analyst can refine it.
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
