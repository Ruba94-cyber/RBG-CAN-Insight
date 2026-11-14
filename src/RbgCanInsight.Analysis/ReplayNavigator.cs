using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Frame-index arithmetic for the offline replay keys (F7 step, F8 animate, F9 run, Esc break) and the
/// "run to next match" break condition. Pure and allocation-free so it is cheap on a 1M-frame trace.
/// </summary>
public static class ReplayNavigator
{
    /// <summary>Animate pace, comparable to Vector's default of roughly three events per second.</summary>
    public const int AnimateIntervalMilliseconds = 300;

    public static int Step(int current, int delta, int frameCount)
    {
        if (frameCount <= 0) return -1;
        var start = Math.Clamp(current, 0, frameCount - 1);
        return (int)Math.Clamp((long)start + delta, 0, frameCount - 1);
    }

    /// <summary>First frame strictly after (direction &gt; 0) or before (direction &lt; 0) <paramref name="fromIndex"/>
    /// that satisfies the break condition, or -1 when none does.</summary>
    public static int FindMatch(IReadOnlyList<CanFrame> frames, int fromIndex, int direction,
        Func<CanFrame, bool> condition, CancellationToken token = default)
    {
        var step = direction >= 0 ? 1 : -1;
        var count = 0;
        for (var index = fromIndex + step; index >= 0 && index < frames.Count; index += step)
        {
            if ((++count & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            if (condition(frames[index])) return index;
        }

        return -1;
    }

    /// <summary>One timer tick of a replay with a break condition: advance up to <paramref name="maxStep"/>
    /// frames but stop on the first matching frame. <paramref name="hit"/> tells whether it stopped on a match.</summary>
    public static int AdvanceUntilMatch(IReadOnlyList<CanFrame> frames, int current, int maxStep,
        Func<CanFrame, bool>? condition, out bool hit)
    {
        hit = false;
        if (frames.Count == 0) return -1;
        var last = Math.Min(frames.Count - 1, (int)Math.Min(int.MaxValue, (long)current + Math.Max(1, maxStep)));
        if (condition is not null)
        {
            for (var index = current + 1; index <= last; index++)
            {
                if (!condition(frames[index])) continue;
                hit = true;
                return index;
            }
        }

        return last;
    }
}
