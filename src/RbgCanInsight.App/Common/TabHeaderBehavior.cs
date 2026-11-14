using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RbgCanInsight.App.Common;

/// <summary>The tab strip scrolls horizontally when the headers do not fit. This keeps the header of the
/// selected tab visible when selection changes (keyboard, code, or a click on a partly hidden header).</summary>
public static class TabHeaderBehavior
{
    public static readonly DependencyProperty BringSelectedIntoViewProperty =
        DependencyProperty.RegisterAttached(
            "BringSelectedIntoView", typeof(bool), typeof(TabHeaderBehavior),
            new PropertyMetadata(false, OnChanged));

    public static bool GetBringSelectedIntoView(DependencyObject element) =>
        (bool)element.GetValue(BringSelectedIntoViewProperty);

    public static void SetBringSelectedIntoView(DependencyObject element, bool value) =>
        element.SetValue(BringSelectedIntoViewProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TabItem item)
            return;
        Selector.RemoveSelectedHandler(item, OnSelected);
        if (e.NewValue is true)
            Selector.AddSelectedHandler(item, OnSelected);
    }

    private static void OnSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not TabItem item)
            return;
        // Defer: on first selection the header has not been measured/arranged yet.
        item.Dispatcher.BeginInvoke(new Action(item.BringIntoView),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
