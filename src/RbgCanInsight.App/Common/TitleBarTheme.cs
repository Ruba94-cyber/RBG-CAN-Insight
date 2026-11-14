using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RbgCanInsight.App.Common;

/// <summary>Applies Windows' immersive dark-mode title bar attribute so the OS-drawn window
/// caption follows the active in-app theme instead of always showing light chrome. This only
/// changes what the OS itself paints (caption background/text/buttons) — it has no effect on the
/// window's client area, which already follows the theme via DynamicResource.</summary>
public static class TitleBarTheme
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Subscribes a window so its caption is painted correctly the first time its native
    /// handle becomes available (before the window is shown, to avoid a flash of the wrong
    /// chrome) using whichever theme is active at that moment.</summary>
    public static void Hook(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window, ThemeManager.UsesDarkChrome);
    }

    public static void Apply(Window window, bool useDarkChrome)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            Apply(handle, useDarkChrome);
        }
    }

    private static void Apply(IntPtr handle, bool useDarkChrome)
    {
        var value = useDarkChrome ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
        {
            // Older Windows 10 builds (pre-20H1) used a different attribute id for the same flag.
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        }
    }
}
