using System.Windows;

namespace RbgCanInsight.App.Common;

/// <summary>
/// Swaps the active color palette dictionary at runtime (Dark/Light/High Contrast) while the
/// control styles in Themes/Styles.xaml stay loaded once and re-resolve live via DynamicResource.
/// Custom-drawn controls that cache brushes at Loaded time (SignalPlotControl,
/// TraceOverviewControl) subscribe to <see cref="ThemeChanged"/> to re-resolve and redraw.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _activePalette;
    private static int _currentThemeIndex;

    public static event EventHandler? ThemeChanged;

    /// <summary>The theme index last passed to <see cref="Apply"/> (0 Dark, 1 Light, 2 High
    /// Contrast), so a window created after startup can paint its own caption correctly on first
    /// show without waiting for a theme change.</summary>
    public static int CurrentThemeIndex => _currentThemeIndex;

    /// <summary>Only the Light theme has a light window background; Dark and the (black-background)
    /// High Contrast theme both read best with dark OS caption chrome.</summary>
    public static bool UsesDarkChrome => _currentThemeIndex != 1;

    public static void Apply(int themeIndex)
    {
        _currentThemeIndex = themeIndex;
        var source = themeIndex switch
        {
            1 => "Themes/Palette.Light.xaml",
            2 => "Themes/Palette.HighContrast.xaml",
            _ => "Themes/Palette.Dark.xaml"
        };

        var palette = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        if (_activePalette is not null)
        {
            dictionaries.Remove(_activePalette);
        }

        dictionaries.Add(palette);
        _activePalette = palette;
        ThemeChanged?.Invoke(null, EventArgs.Empty);

        foreach (Window window in Application.Current.Windows)
        {
            TitleBarTheme.Apply(window, UsesDarkChrome);
        }
    }
}
