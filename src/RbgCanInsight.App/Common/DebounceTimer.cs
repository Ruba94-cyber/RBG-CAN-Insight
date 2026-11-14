using System.Windows.Threading;

namespace RbgCanInsight.App.Common;

/// <summary>Coalesces a burst of rapid calls (typically one per keystroke in a live search box)
/// into a single action fired after a short quiet period, so a filter rebuild over a large
/// signal/DBC/mapping catalog runs once per typing pause instead of once per character.</summary>
public sealed class DebounceTimer
{
    private readonly DispatcherTimer _timer;
    private Action? _pendingAction;

    public DebounceTimer(TimeSpan delay)
    {
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            var action = _pendingAction;
            _pendingAction = null;
            action?.Invoke();
        };
    }

    public DebounceTimer() : this(TimeSpan.FromMilliseconds(150))
    {
    }

    /// <summary>Schedules <paramref name="action"/> to run after the delay, replacing any action
    /// already pending — only the last call in a burst actually runs.</summary>
    public void Trigger(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _pendingAction = action;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Cancel an obsolete pending search after an explicit
    /// navigation replaces the catalog scope. A delayed rebuild must not
    /// erase the destination selection or resurrect a previous filter.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _pendingAction = null;
    }
}
