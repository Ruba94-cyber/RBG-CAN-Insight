using System.IO;
using System.Text.Json;

namespace RbgCanInsight.App.Common;

/// <summary>Small persisted store for cross-session app-level preferences (selected theme,
/// whether the first-run tour has been shown) that are not tied to a specific workspace preset
/// or table layout.</summary>
public static class AppSettingsStore
{
    private sealed record Settings(
        int ThemeIndex,
        bool OnboardingShown = false,
        IReadOnlyList<string>? WorkspaceOrder = null,
        bool UtilityPanelExpanded = false,
        double TraceOverviewHeight = 48,
        int SelectedNavIndex = 9, // Fresh install: simple CAN signal explorer; existing saved workspace stays unchanged.
        bool QuickPlotDetailsExpanded = true,
        double PlaybackSpeed = 1,
        double ByteMatrixOverviewHeight = 140,
        int TraceTimeDisplayModeIndex = 0,
        string? ReferenceTracePath = null,
        bool SignalsLinearInterpolation = false,
        int SignalsYAxisModeIndex = 0,
        bool SignalsInlineLegend = false,
        bool SignalsCumulativeReveal = false,
        int SettingsVersion = 0);

    /// <summary>Bumped when a stored value needs a one-time migration. Files written before 5.0.3 have no marker (0).</summary>
    private const int CurrentSettingsVersion = 1;

    /// <summary>The stable reference (baseline) trace the analyst last compared against, so a new
    /// capture can be checked against it again with one click.</summary>
    public static string? LoadReferenceTracePath() => Load().ReferenceTracePath;
    public static void SaveReferenceTracePath(string? path) => Save(Load() with { ReferenceTracePath = path });

    private static string SettingsPath => Path.Combine(AppStorage.ConfigDirectory, "app-settings.json");

    public static int LoadThemeIndex() => Load().ThemeIndex;

    public static void SaveThemeIndex(int themeIndex) => Save(Load() with { ThemeIndex = themeIndex });

    /// <summary>True once the first-launch tour has been completed or skipped.</summary>
    public static bool LoadOnboardingShown() => Load().OnboardingShown;

    public static void SaveOnboardingShown() => Save(Load() with { OnboardingShown = true });

    public static IReadOnlyList<string> LoadWorkspaceOrder() => Load().WorkspaceOrder ?? [];
    public static void SaveWorkspaceOrder(IReadOnlyList<string> order) =>
        Save(Load() with { WorkspaceOrder = order.ToArray() });

    public static bool LoadUtilityPanelExpanded() => Load().UtilityPanelExpanded;
    public static void SaveUtilityPanelExpanded(bool expanded) =>
        Save(Load() with { UtilityPanelExpanded = expanded });

    /// <summary>Saved density-strip height. Before 5.0.3 the default was 190, which starved the frame grid. Files
    /// without a settings-version marker (written by an older build) are migrated once: a stored 190 becomes the
    /// compact default. After that the marker is stamped, so a height the user picks later is never overridden.</summary>
    public static double LoadTraceOverviewHeight()
    {
        var settings = Load();
        if (settings.SettingsVersion < CurrentSettingsVersion)
        {
            settings = settings with
            {
                TraceOverviewHeight = Math.Abs(settings.TraceOverviewHeight - 190) < 1 ? 48 : settings.TraceOverviewHeight,
                SettingsVersion = CurrentSettingsVersion
            };
            Save(settings);
        }

        return Math.Clamp(settings.TraceOverviewHeight, 36, 520);
    }
    public static void SaveTraceOverviewHeight(double height) =>
        Save(Load() with { TraceOverviewHeight = Math.Clamp(height, 36, 520) });

    public static double LoadByteMatrixOverviewHeight() => Math.Clamp(Load().ByteMatrixOverviewHeight, 70, 420);
    public static void SaveByteMatrixOverviewHeight(double height) =>
        Save(Load() with { ByteMatrixOverviewHeight = Math.Clamp(height, 70, 420) });

    /// <summary>Trace grid Time column mode: 0 Absolute, 1 Relative to trace start,
    /// 2 Delta from previous frame, 3 Delta from previous frame of the same identifier.</summary>
    public static int LoadTraceTimeDisplayModeIndex() => Load().TraceTimeDisplayModeIndex;
    public static void SaveTraceTimeDisplayModeIndex(int index) =>
        Save(Load() with { TraceTimeDisplayModeIndex = index });

    /// <summary>The workspace tab open when the app was last closed, so relaunching returns to
    /// where the operator left off instead of always starting at the Dashboard.</summary>
    public static int LoadSelectedNavIndex() => Load().SelectedNavIndex;
    public static void SaveSelectedNavIndex(int index) => Save(Load() with { SelectedNavIndex = index });

    /// <summary>Whether the Quick Plot window's details panel (DBC description, scaling, value
    /// table, observed statistics) was left open, remembered across every Quick Plot window since
    /// it is a per-feature preference, not a per-signal one.</summary>
    public static bool LoadQuickPlotDetailsExpanded() => Load().QuickPlotDetailsExpanded;
    public static void SaveQuickPlotDetailsExpanded(bool expanded) =>
        Save(Load() with { QuickPlotDetailsExpanded = expanded });

    /// <summary>Shared playback speed for every offline transport control (Trace, Signals &amp;
    /// Plot, Byte Matrix, Quick Plot) — see <see cref="PlaybackSpeedStore"/>.</summary>
    public static double LoadPlaybackSpeed() => Load().PlaybackSpeed;
    public static void SavePlaybackSpeed(double speed) => Save(Load() with { PlaybackSpeed = speed });

    /// <summary>Only visual chart preferences persist across launches;
    /// search and visibility filters deliberately start clear, so users
    /// cannot accidentally hide recorded evidence on the next session.</summary>
    public static (bool Linear, int YAxisMode, bool Legend, bool Cumulative)
        LoadSignalsChartPreferences()
    {
        var s = Load();
        return (s.SignalsLinearInterpolation,
            Math.Clamp(s.SignalsYAxisModeIndex, 0, 2),
            s.SignalsInlineLegend, s.SignalsCumulativeReveal);
    }

    public static void SaveSignalsChartPreferences(
        bool linear, int yAxisMode, bool legend, bool cumulative)
    {
        var s = Load();
        Save(s with
        {
            SignalsLinearInterpolation = linear,
            SignalsYAxisModeIndex = Math.Clamp(yAxisMode, 0, 2),
            SignalsInlineLegend = legend,
            SignalsCumulativeReveal = cumulative
        });
    }

    private static Settings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings(0)
                : new Settings(0);
        }
        catch
        {
            return new Settings(0);
        }
    }

    private static void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // Best effort: settings persistence is convenience-only.
        }
    }
}
