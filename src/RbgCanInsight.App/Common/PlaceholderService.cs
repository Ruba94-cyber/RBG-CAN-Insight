using System.Windows;

namespace RbgCanInsight.App.Common;

/// <summary>
/// Attached placeholder text for TextBoxes (WPF has no native one). The app-wide TextBox
/// template in Themes/Styles.xaml overlays this text in muted ink while the box is empty, so
/// search and filter boxes visibly say what they are instead of sitting as anonymous empty
/// inputs. Set it in XAML: <c>common:PlaceholderService.Text="🔍 Search signals…"</c>.
/// </summary>
public static class PlaceholderService
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(PlaceholderService),
        new FrameworkPropertyMetadata(string.Empty));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
}
