using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

public enum TraceEditKind { Cut, Shift, SplitAtGaps, RemapChannel }

/// <summary>One edit request in user units (seconds, 1-based channel numbers as the Trace grid shows them).</summary>
public sealed record TraceEditRequest(
    TraceEditKind Kind,
    double FromSeconds = 0, double ToSeconds = 0,
    double ShiftSeconds = 0, double GapSeconds = 0,
    int FromChannel = 1, int ToChannel = 1);

/// <summary>Result: one segment for Cut/Shift/Remap, several for SplitAtGaps. Never modifies the input.</summary>
public sealed record TraceEditOutcome(IReadOnlyList<List<CanFrame>> Segments)
{
    public int FrameCount => Segments.Sum(s => s.Count);
    public double? FirstSeconds => FrameCount > 0
        ? Segments.Where(s => s.Count > 0).Min(s => s[0].TimestampNanoseconds) / 1e9 : null;
    public double? LastSeconds => FrameCount > 0
        ? Segments.Where(s => s.Count > 0).Max(s => s[^1].TimestampNanoseconds) / 1e9 : null;
}

public static class TraceEditPlan
{
    /// <exception cref="ArgumentException">Invalid parameters (message is user-presentable).</exception>
    public static TraceEditOutcome Apply(IReadOnlyList<CanFrame> frames, TraceEditRequest r)
    {
        ArgumentNullException.ThrowIfNull(frames);
        try
        {
            switch (r.Kind)
            {
                case TraceEditKind.Cut:
                    if (r.ToSeconds < r.FromSeconds) throw new ArgumentException("'To' must not be before 'From'.");
                    // Both ends are inclusive for the user: add 1 ns so a frame exactly at 'To' is kept.
                    return new([TraceTools.Cut(frames, ToNs(r.FromSeconds), ToNs(r.ToSeconds) + 1)]);
                case TraceEditKind.Shift:
                    return new([TraceTools.ShiftTime(frames, ToNs(r.ShiftSeconds))]);
                case TraceEditKind.SplitAtGaps:
                    if (!(r.GapSeconds > 0)) throw new ArgumentException("The gap must be greater than zero.");
                    return new(TraceTools.SplitAtGaps(frames, Math.Max(1, ToNs(r.GapSeconds))));
                case TraceEditKind.RemapChannel:
                    if (r.FromChannel < 1 || r.ToChannel < 1) throw new ArgumentException("Channel numbers start at 1.");
                    return new([TraceTools.RemapChannels(frames, new Dictionary<int, int> { [r.FromChannel - 1] = r.ToChannel - 1 })]);
                default:
                    throw new ArgumentException("Unknown operation.");
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ArgumentException(ex.Message.Split('\n')[0], ex);
        }
    }

    public static long ToNs(double seconds)
    {
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 9e9) throw new ArgumentException("Time value is out of range.");
        return (long)Math.Round(seconds * 1e9, MidpointRounding.AwayFromZero);
    }

    /// <summary>Parses a number with '.' or ',' as decimal separator.</summary>
    public static bool TryParseNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    /// <summary>"name_part01.ext" for split output, otherwise the path unchanged.</summary>
    public static string SegmentPath(string path, int index, int total) =>
        total <= 1 ? path
        : Path.Combine(Path.GetDirectoryName(path) ?? "",
            $"{Path.GetFileNameWithoutExtension(path)}_part{index + 1:D2}{Path.GetExtension(path)}");
}
