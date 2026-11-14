using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RbgCanInsight.App.Common;

/// <summary>
/// Moves an existing panel into its own window (and back on close) so secondary
/// detail never has to share a cramped page. The same visual tree and bindings
/// are reused; a "docked in window" placeholder keeps the page layout stable.
/// </summary>
public static class PopOutHost
{
    private static readonly Dictionary<FrameworkElement, Window> Open = [];

    public static Window? Toggle(FrameworkElement panel, Window owner, string title,
        double width = 1000, double height = 640)
    {
        if (Open.TryGetValue(panel, out var existing))
        {
            existing.Activate();
            return null;
        }

        var parent = panel.Parent;
        if (parent is null)
            return null;

        Action restore;
        var placeholder = new Button
        {
            Content = "Open in a separate window — click to dock it back here",
            Margin = panel is FrameworkElement fe ? fe.Margin : default,
            Padding = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Top
        };
        CopyGridPlacement(panel, placeholder);

        switch (parent)
        {
            case Panel host:
            {
                var index = host.Children.IndexOf(panel);
                host.Children.RemoveAt(index);
                host.Children.Insert(index, placeholder);
                restore = () =>
                {
                    var i = host.Children.IndexOf(placeholder);
                    host.Children.RemoveAt(i);
                    host.Children.Insert(i, panel);
                };
                break;
            }
            case Decorator deco:
                deco.Child = placeholder;
                restore = () => deco.Child = panel;
                break;
            case ContentControl cc:
                cc.Content = placeholder;
                restore = () => cc.Content = panel;
                break;
            default:
                return null;
        }

        var window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 520,
            MinHeight = 320,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = owner.DataContext,
            Icon = owner.Icon
        };
        window.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        TitleBarTheme.Hook(window);
        Open[panel] = window;
        // Detach the visual from its page before parenting it into the window.
        var margin = panel.Margin;
        panel.Margin = new Thickness(10);
        window.Content = panel;

        var closed = false;
        void Dock()
        {
            if (closed) return;
            closed = true;
            window.Content = null;
            panel.Margin = margin;
            restore();
            Open.Remove(panel);
            if (window.IsVisible) window.Close();
        }
        window.Closed += (_, _) => Dock();
        placeholder.Click += (_, _) => window.Close();
        window.Show();
        return window;
    }

    private static void CopyGridPlacement(FrameworkElement from, FrameworkElement to)
    {
        Grid.SetRow(to, Grid.GetRow(from));
        Grid.SetColumn(to, Grid.GetColumn(from));
        Grid.SetRowSpan(to, Grid.GetRowSpan(from));
        Grid.SetColumnSpan(to, Grid.GetColumnSpan(from));
        DockPanel.SetDock(to, DockPanel.GetDock(from));
    }
}
