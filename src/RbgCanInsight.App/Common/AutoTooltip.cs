using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RbgCanInsight.App.Common;

/// <summary>
/// Attached behavior that shows a TextBlock's full text as a tooltip automatically, but only
/// when <see cref="TextBlock.TextTrimming"/> is actually clipping it — text that already fits
/// never gets a tooltip. Enabled once from the default TextBlock style in Theme.xaml, so every
/// truncated label anywhere in the app (grid cells, nav labels, dashboard tiles, dialogs, DBC
/// comments, ECU names...) gets this behavior without being wired field by field.
/// </summary>
public static class AutoTooltip
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(AutoTooltip),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static readonly DependencyProperty OwnsTooltipProperty =
        DependencyProperty.RegisterAttached("OwnsTooltip", typeof(bool), typeof(AutoTooltip), new PropertyMetadata(false));

    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            textBlock.SizeChanged += OnTextBlockInvalidated;
            textBlock.Unloaded += OnTextBlockUnloaded;
            TextDescriptor.AddValueChanged(textBlock, OnTextBlockInvalidated);
            UpdateTooltip(textBlock);
        }
        else
        {
            Detach(textBlock);
        }
    }

    private static void OnTextBlockUnloaded(object sender, RoutedEventArgs e) => Detach((TextBlock)sender);

    private static void Detach(TextBlock textBlock)
    {
        textBlock.SizeChanged -= OnTextBlockInvalidated;
        textBlock.Unloaded -= OnTextBlockUnloaded;
        TextDescriptor.RemoveValueChanged(textBlock, OnTextBlockInvalidated);
    }

    private static void OnTextBlockInvalidated(object? sender, EventArgs e) => UpdateTooltip((TextBlock)sender!);

    private static void UpdateTooltip(TextBlock textBlock)
    {
        var text = textBlock.Text;
        if (string.IsNullOrEmpty(text) || textBlock.ActualWidth <= 0 || textBlock.TextWrapping != TextWrapping.NoWrap)
        {
            ClearOwnTooltip(textBlock);
            return;
        }

        var typeface = new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            textBlock.FlowDirection,
            typeface,
            textBlock.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(textBlock).PixelsPerDip);

        var availableWidth = textBlock.ActualWidth - textBlock.Padding.Left - textBlock.Padding.Right;
        if (formatted.Width > availableWidth + 0.5)
        {
            ToolTipService.SetToolTip(textBlock, text);
            textBlock.SetValue(OwnsTooltipProperty, true);
        }
        else
        {
            ClearOwnTooltip(textBlock);
        }
    }

    private static void ClearOwnTooltip(TextBlock textBlock)
    {
        // Only remove a tooltip this behavior set itself — never touch one bound explicitly
        // by a specific view (e.g. an ElementStyle that always shows a hint tooltip).
        if ((bool)textBlock.GetValue(OwnsTooltipProperty))
        {
            ToolTipService.SetToolTip(textBlock, null);
            textBlock.SetValue(OwnsTooltipProperty, false);
        }
    }
}
