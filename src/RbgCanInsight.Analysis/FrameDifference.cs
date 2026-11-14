using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>One row of the frame difference table: a label plus one cell per compared frame.</summary>
public sealed record FrameDifferenceRow(string Label, IReadOnlyList<string> Cells, bool Differs, string Summary = "");

public sealed record FrameDifferenceResult(
    IReadOnlyList<string> FrameHeaders,
    IReadOnlyList<FrameDifferenceRow> Properties,
    IReadOnlyList<FrameDifferenceRow> Bytes,
    IReadOnlyList<FrameDifferenceRow> Signals)
{
    public int DifferingBytes => Bytes.Count(row => row.Differs);
    public int DifferingSignals => Signals.Count(row => row.Differs);
}

/// <summary>
/// Vector-style Difference + Statistic view for a handful of chosen trace events: compares frame
/// properties, every payload byte, and (with a database) every decoded signal, and summarises each
/// signal's min / max / mean over exactly the chosen frames.
/// </summary>
public static class FrameDifference
{
    /// <summary>Upper bound on compared frames; wider tables stop being readable.</summary>
    public const int MaxFrames = 12;

    public static FrameDifferenceResult Compute(
        IReadOnlyList<(int FrameIndex, CanFrame Frame)> frames,
        Func<CanFrame, IReadOnlyList<SignalValue>>? decode,
        long originNanoseconds)
    {
        var selected = frames.Take(MaxFrames).ToArray();
        var headers = selected
            .Select(item => $"#{item.FrameIndex + 1:N0}")
            .ToArray();

        FrameDifferenceRow Property(string label, Func<CanFrame, string> read)
        {
            var cells = selected.Select(item => read(item.Frame)).ToArray();
            return new FrameDifferenceRow(label, cells, cells.Distinct(StringComparer.Ordinal).Count() > 1);
        }

        var properties = new List<FrameDifferenceRow>
        {
            Property("Time [s]", f => ((f.TimestampNanoseconds - originNanoseconds) / 1e9).ToString("F6", CultureInfo.InvariantCulture)),
            Property("Channel", f => (f.Channel + 1).ToString(CultureInfo.InvariantCulture)),
            Property("ID", f => f.Identifier.ToString(f.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture)),
            Property("Length", f => f.DataLength.ToString(CultureInfo.InvariantCulture)),
            Property("Kind", Kind)
        };

        // Time always differs between distinct events; keep it informational rather than a difference.
        properties[0] = properties[0] with { Differs = false };

        var maxLength = selected.Length == 0 ? 0 : selected.Max(item => (int)item.Frame.DataLength);
        var bytes = new List<FrameDifferenceRow>();
        for (var index = 0; index < maxLength; index++)
        {
            var cells = selected.Select(item => index < item.Frame.DataLength
                ? item.Frame.GetByte(index).ToString("X2", CultureInfo.InvariantCulture)
                : "--").ToArray();
            var differs = cells.Distinct(StringComparer.Ordinal).Count() > 1;
            bytes.Add(new FrameDifferenceRow($"Byte {index}", cells, differs,
                differs ? ChangedBits(selected.Select(item => item.Frame), index) : string.Empty));
        }

        var signals = new List<FrameDifferenceRow>();
        if (decode is not null && selected.Length > 0)
        {
            var perFrame = selected.Select(item => decode(item.Frame)).ToArray();
            var names = perFrame.SelectMany(values => values)
                .Select(value => (value.Message, value.Signal, value.Unit))
                .Distinct()
                .OrderBy(name => name.Message, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name.Signal, StringComparer.OrdinalIgnoreCase);
            foreach (var (message, signal, unit) in names)
            {
                var values = perFrame.Select(list => list.Where(item =>
                        item.Message == message && item.Signal == signal)
                    .Select(item => (double?)item.PhysicalValue).FirstOrDefault()).ToArray();
                var present = values.Where(value => value.HasValue && double.IsFinite(value.Value))
                    .Select(value => value!.Value).ToArray();
                var cells = values.Select(value => value.HasValue
                    ? value.Value.ToString("G6", CultureInfo.InvariantCulture)
                    : "-").ToArray();
                var differs = cells.Distinct(StringComparer.Ordinal).Count() > 1;
                var unitSuffix = string.IsNullOrWhiteSpace(unit) ? string.Empty : " " + unit;
                var summary = present.Length == 0
                    ? string.Empty
                    : $"min {present.Min():G6} · max {present.Max():G6} · mean {present.Average():G6}{unitSuffix} (n={present.Length})";
                signals.Add(new FrameDifferenceRow($"{message}.{signal}", cells, differs, summary));
            }
        }

        return new FrameDifferenceResult(headers, properties, bytes, signals);
    }

    private static string Kind(CanFrame frame)
    {
        var parts = new List<string>();
        if (frame.IsError) parts.Add("error");
        if (frame.Flags.HasFlag(CanFrameFlags.Remote)) parts.Add("remote");
        if (frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate)) parts.Add("FD");
        parts.Add(frame.IsExtended ? "29-bit" : "11-bit");
        return string.Join(' ', parts);
    }

    /// <summary>Bits (7 = MSB) that are not the same in every frame, e.g. "bits 0,3".</summary>
    private static string ChangedBits(IEnumerable<CanFrame> frames, int byteIndex)
    {
        var values = frames.Where(frame => byteIndex < frame.DataLength).Select(frame => frame.GetByte(byteIndex)).ToArray();
        if (values.Length < 2) return string.Empty;
        var changed = 0;
        foreach (var value in values) changed |= value ^ values[0];
        var bits = Enumerable.Range(0, 8).Where(bit => (changed & (1 << bit)) != 0).ToArray();
        return bits.Length == 0 ? string.Empty : "differing bits: " + string.Join(',', bits);
    }
}
