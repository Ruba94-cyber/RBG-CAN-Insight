using System.IO;
using System.Text.Json;
using System.Windows;

namespace RbgCanInsight.App.Common;

/// <summary>
/// Multi-monitor-safe window placement. The app never relies on the OS/WPF default startup
/// location alone: it persists the last known-good position and, on every launch, validates
/// that position against the monitors actually connected right now. If the saved rectangle no
/// longer falls within the current virtual desktop (e.g. an external monitor used last session
/// is unplugged, or the app is a portable copy moved to a new machine), it falls back to a
/// rectangle centered on the primary monitor's work area instead of an arbitrary or off-screen
/// position.
/// </summary>
public static class WindowPlacementStore
{
    private sealed record Placement(double Left, double Top, double Width, double Height, bool IsMaximized);

    private static string PlacementPath => Path.Combine(AppStorage.ConfigDirectory, "window-placement.json");

    private static string NamedPlacementPath(string key) =>
        Path.Combine(AppStorage.ConfigDirectory, $"window-placement-{key}.json");

    /// <summary>Resolves where the window should be placed at startup: the saved position if it
    /// is still valid for a currently connected monitor, otherwise centered on the primary
    /// monitor's work area.</summary>
    public static (Rect Bounds, bool Maximized) ResolveStartupPlacement(double defaultWidth, double defaultHeight)
    {
        var saved = Load();
        if (saved is not null)
        {
            var savedBounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
            if (IsWithinVirtualDesktop(savedBounds))
            {
                return (savedBounds, saved.IsMaximized);
            }
        }

        return (CenteredOnPrimaryWorkArea(defaultWidth, defaultHeight), false);
    }

    /// <summary>Rectangle centered on the primary monitor's work area, clamped so it never
    /// exceeds that monitor's available size (e.g. on a smaller secondary-turned-primary display).</summary>
    public static Rect CenteredOnPrimaryWorkArea(double width, double height)
    {
        var workArea = SystemParameters.WorkArea;
        var clampedWidth = Math.Min(width, workArea.Width);
        var clampedHeight = Math.Min(height, workArea.Height);
        var left = workArea.Left + (workArea.Width - clampedWidth) / 2;
        var top = workArea.Top + (workArea.Height - clampedHeight) / 2;
        return new Rect(left, top, clampedWidth, clampedHeight);
    }

    /// <summary>True when the rectangle overlaps the combined bounds of every currently
    /// connected monitor (WPF's virtual desktop), so a window there would actually be visible.</summary>
    public static bool IsWithinVirtualDesktop(Rect bounds)
    {
        var virtualDesktop = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        // Require a visible sliver of the title bar area, not just any overlap, so a window that
        // is almost entirely off-screen still counts as "lost" and gets recentered.
        var visible = Rect.Intersect(virtualDesktop, bounds);
        return !visible.IsEmpty && visible.Width >= 80 && visible.Height >= 40;
    }

    public static void Save(Window window) => SaveTo(window, PlacementPath);

    private static Placement? Load() => LoadFrom(PlacementPath);

    /// <summary>Same persistence for a secondary window kind (e.g. the Quick Plot window), kept in
    /// its own file under a caller-chosen key so it never collides with the main window's saved
    /// placement or with another secondary window kind.</summary>
    public static void SaveNamed(Window window, string key) => SaveTo(window, NamedPlacementPath(key));

    /// <summary>Only width/height/maximized are meaningful for a window opened with
    /// <c>WindowStartupLocation.CenterOwner</c> — position is deliberately not restored so it
    /// keeps centering over whichever owner window is active.</summary>
    public static (double Width, double Height, bool Maximized)? ResolveNamedSize(string key)
    {
        var saved = LoadFrom(NamedPlacementPath(key));
        return saved is null ? null : (saved.Width, saved.Height, saved.IsMaximized);
    }

    private static void SaveTo(Window window, string path)
    {
        try
        {
            var isMaximized = window.WindowState == WindowState.Maximized;
            var bounds = isMaximized ? window.RestoreBounds : new Rect(window.Left, window.Top, window.Width, window.Height);
            var placement = new Placement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, isMaximized);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(placement));
        }
        catch
        {
            // Window placement persistence is best-effort convenience only.
        }
    }

    private static Placement? LoadFrom(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Placement>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }
}
