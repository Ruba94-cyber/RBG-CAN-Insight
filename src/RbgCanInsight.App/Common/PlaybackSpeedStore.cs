namespace RbgCanInsight.App.Common;

/// <summary>Single shared playback-speed value for every offline transport control in the app
/// (Trace, Signals &amp; Plot, Byte Matrix and the dedicated Quick Plot window). Previously each
/// view kept its own independent speed, so replaying the same moment looked different depending
/// on which view was open last. Changing the speed anywhere updates every other open view
/// immediately and the choice survives an app restart, the same way <see cref="ThemeManager"/>
/// shares and persists the active theme.</summary>
public static class PlaybackSpeedStore
{
    private static double _current = Math.Clamp(AppSettingsStore.LoadPlaybackSpeed(), 0.1, 100);

    public static event EventHandler? Changed;

    public static double Current
    {
        get => _current;
        set
        {
            var clamped = Math.Clamp(value, 0.1, 100);
            if (Math.Abs(clamped - _current) < 1e-9)
            {
                return;
            }

            _current = clamped;
            AppSettingsStore.SavePlaybackSpeed(clamped);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
