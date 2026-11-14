using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RbgCanInsight.App.Common;

/// <summary>Shortens long text (file paths) in the middle, keeping the start and the file name.
/// The parameter is the maximum character count (default 48).</summary>
public sealed class MiddleEllipsisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        var max = int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 48;
        if (max < 8 || text.Length <= max) return text;
        var tail = (max - 1) * 2 / 3;
        var head = max - 1 - tail;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Makes sure no DataGrid column is narrower than its own header text, so headers are never
/// cut off at small window sizes (the grid scrolls horizontally instead).</summary>
public static class GridLayoutHelpers
{
    public static void EnsureHeadersFit(DependencyObject root)
    {
        foreach (var grid in FindGrids(root))
        {
            var typeface = new Typeface(grid.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
            foreach (var column in grid.Columns)
            {
                if (column.Header is not string header || header.Length == 0) continue;
                var text = new FormattedText(header, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, grid.FontSize, Brushes.Black, dpi);
                // 8+8 header padding, 1 border, plus room for the sort glyph.
                var needed = Math.Ceiling(text.Width) + 34;
                if (column.MinWidth < needed) column.MinWidth = needed;
                if (!column.Width.IsStar && !column.Width.IsAuto && column.Width.Value < needed)
                    column.Width = new DataGridLength(needed);
            }
        }
    }

    private static IEnumerable<DataGrid> FindGrids(DependencyObject parent)
    {
        if (parent is DataGrid grid) yield return grid;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            foreach (var found in FindGrids(child)) yield return found;
        }
    }
}
