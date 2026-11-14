using System.Windows;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

public partial class PresetLibraryWindow : Window
{
    private readonly MainViewModel _viewModel;

    public PresetLibraryWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        TitleBarTheme.Hook(this);
        DataContext = viewModel;
        _viewModel.RefreshPresetLibrary();
    }

    private async void OnLoadClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PresetLibraryRowViewModel row)
        {
            return;
        }

        await _viewModel.LoadPresetFileAsync(row.Path);
        Close();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PresetLibraryRowViewModel row)
        {
            _viewModel.RemovePresetLibraryEntry(row.Path);
        }
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "RBG CAN Insight preset (*.rbgpreset)|*.rbgpreset|JSON (*.json)|*.json|All files (*.*)|*.*",
            Title = "Load workspace preset"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _viewModel.LoadPresetFileAsync(dialog.FileName);
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
