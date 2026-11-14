using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

/// <summary>Ctrl+K universal search overlay: modal, borderless, closes on Esc, losing focus, or
/// activating a result.</summary>
public partial class GlobalSearchWindow : Window
{
    private readonly GlobalSearchViewModel _search;
    private bool _closing;

    public GlobalSearchWindow(MainViewModel main)
    {
        InitializeComponent();
        TitleBarTheme.Hook(this);
        _search = new GlobalSearchViewModel(main);
        DataContext = _search;
        Loaded += (_, _) => QueryBox.Focus();
    }

    private void OnQueryPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _search.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                _search.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                // Activating a result can shift the main window's content/focus, which may fire
                // Deactivated on this window before we get to closing it ourselves — CloseOnce()
                // guards so the two never race into calling Close() on an already-closing Window.
                if (_search.ActivateSelected())
                {
                    CloseOnce();
                }
                e.Handled = true;
                break;
            case Key.Escape:
                CloseOnce();
                e.Handled = true;
                break;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedIndex >= 0)
        {
            _search.SelectedIndex = ResultsList.SelectedIndex;
            if (_search.ActivateSelected())
            {
                CloseOnce();
            }
        }
    }

    /// <summary>Direct mouse action: an analyst should not have to
    /// double-click a search hit or discover Enter to open it.</summary>
    private void OnResultOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlobalSearchResult hit })
        {
            hit.Activate();
            CloseOnce();
        }
    }

    private void OnClearQueryClick(object sender, RoutedEventArgs e)
    {
        QueryBox.Clear();
        QueryBox.Focus();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseOnce();

    private void OnDeactivated(object sender, EventArgs e) => CloseOnce();

    /// <summary>Covers closes this window did not start itself (Alt+F4, the owner shutting
    /// down): mid-close WPF deactivates the window, which used to re-enter Close() through
    /// OnDeactivated and throw "cannot call Close while a Window is closing".</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void CloseOnce()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }
}
