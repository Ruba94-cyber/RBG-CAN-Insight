using System.Globalization;
using System.Text;
using RbgCanInsight.Analysis.Heuristics;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.E2E;

/// <summary>One CAN ID present in the trace.</summary>
public sealed record E2eIdEntry(int Channel, uint Id, bool Extended, int FrameCount)
{
    public string IdText => Extended ? $"0x{Id:X8}" : $"0x{Id:X3}";
    public override string ToString() => $"Ch {Channel + 1}  {IdText}  ({FrameCount:N0} {(FrameCount == 1 ? "frame" : "frames")})";
}

/// <summary>Pre-fill suggested by the counter/checksum heuristics.</summary>
public sealed record E2eHint(E2eProfile Profile, int CounterBitOffset, int CrcByte, string Text);

public enum E2eChip { Green, Amber, Red }

/// <summary>Plain-language outcome of one check.</summary>
public sealed record E2eOutcome(E2eChip Chip, string Headline, string Details);

/// <summary>One grid row.</summary>
public sealed record E2eRow(
    int Message, string Time, string Status, string Counter, string ReceivedCrc, string ExpectedCrc,
    int Lost, string GapMs, bool IsProblem);

/// <summary>UI-independent logic behind the E2E check window.</summary>
public static class E2eCheckLogic
{
    public static IReadOnlyList<E2eIdEntry> ListIds(IReadOnlyList<CanFrame> frames)
    {
        var map = new Dictionary<(int, uint, bool), int>();
        foreach (var f in frames)
        {
            if (f.IsError || f.Flags.HasFlag(CanFrameFlags.Remote)) continue;
            var k = (f.Channel, f.Identifier, f.IsExtended);
            map[k] = map.GetValueOrDefault(k) + 1;
        }
        return map.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2).ThenBy(p => p.Key.Item3)
            .Select(p => new E2eIdEntry(p.Key.Item1, p.Key.Item2, p.Key.Item3, p.Value)).ToList();
    }

    public static bool Matches(E2eIdEntry e, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var t = search.Trim();
        var s = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t;
        return e.IdText.Contains(s, StringComparison.OrdinalIgnoreCase) ||
               $"ch {e.Channel + 1}".Contains(t, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Frames of one stream in trace order (data frames only).</summary>
    public static List<E2eFrame> StreamFrames(IReadOnlyList<CanFrame> frames, E2eIdEntry id)
    {
        var list = new List<E2eFrame>();
        foreach (var f in frames)
        {
            if (f.IsError || f.Flags.HasFlag(CanFrameFlags.Remote)) continue;
            if (f.Channel != id.Channel || f.Identifier != id.Id || f.IsExtended != id.Extended) continue;
            list.Add(new E2eFrame(f.Channel, f.Identifier, f.IsExtended, f.TimestampNanoseconds / 1e9, f.GetPayload()));
        }
        return list;
    }

    public static int DominantLength(IReadOnlyList<E2eFrame> frames) =>
        frames.Count == 0 ? 8 : frames.GroupBy(f => f.Data.Length)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;

    public static (int CrcByte, int CounterBit) DefaultLayout(E2eProfile p) =>
        p == E2eProfile.P05 ? (0, 16) : (0, 8);

    public static bool TryParseHex(string? text, out ushort value)
    {
        value = 0;
        var s = (text ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length is >= 1 and <= 4 &&
               ushort.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    public static (E2eConfig? Config, string? Error) BuildConfig(
        E2eIdEntry id, E2eProfile profile, string? dataIdHex, string? cycleMs, string? crcByte, string? counterBit,
        int payloadLength)
    {
        if (!TryParseHex(dataIdHex, out var dataId))
            return (null, "Enter the Data ID as 1 to 4 hex digits (for example 1A2B).");
        double? cycle = null;
        if (!string.IsNullOrWhiteSpace(cycleMs))
        {
            if (!double.TryParse(cycleMs.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0)
                return (null, "The cycle time must be a positive number of milliseconds, or empty.");
            cycle = ms / 1000.0;
        }
        var def = DefaultLayout(profile);
        var crc = def.CrcByte;
        var ctr = def.CounterBit;
        if (!string.IsNullOrWhiteSpace(crcByte) && !int.TryParse(crcByte.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out crc))
            return (null, "The CRC byte must be a whole number (0 is the first byte).");
        if (!string.IsNullOrWhiteSpace(counterBit) && !int.TryParse(counterBit.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ctr))
            return (null, "The counter bit must be a whole number.");
        var cfg = new E2eConfig
        {
            Channel = id.Channel, Id = id.Id, Extended = id.Extended, Profile = profile, DataId = dataId,
            CrcBitOffset = crc * 8, CounterBitOffset = ctr, PayloadLength = payloadLength, CycleTimeSeconds = cycle
        };
        var err = E2eChecker.Validate(cfg);
        return err is null ? (cfg, null) : (null, err + " Adjust the CRC byte, counter bit or profile.");
    }

    public static E2eHint? SuggestHint(IReadOnlyList<E2eFrame> stream)
    {
        if (stream.Count < 16) return null;
        var a = ByteSignalHeuristics.Analyze(0, 0, false, stream.Select(f => f.Data).ToList());
        if (a.Counters.Count == 0 || a.Checksums.Count == 0) return null;
        var crc = a.Checksums.OrderByDescending(c => c.Fit).First();
        var counter = a.Counters
            .Where(c => c.StartBit / 8 != crc.ByteIndex &&
                        ((c.BitLength == 4 && c.StartBit % 8 is 0 or 4) || (c.BitLength == 8 && c.StartBit % 8 == 0)))
            .OrderByDescending(c => c.Confidence).FirstOrDefault();
        if (counter is null) return null;
        var profile = counter.BitLength == 8 ? E2eProfile.P05
            : crc.Algorithm.Contains("2F", StringComparison.Ordinal) ? E2eProfile.P02 : E2eProfile.P01;
        var name = profile switch { E2eProfile.P05 => "Profile 5", E2eProfile.P02 => "Profile 2", _ => "Profile 1" };
        var bits = $"{counter.StartBit}-{counter.StartBit + counter.BitLength - 1}";
        return new E2eHint(profile, counter.StartBit, crc.ByteIndex,
            $"Detected a counter at bits {bits} and a checksum in byte {crc.ByteIndex} - try {name}. Click to fill the fields.");
    }

    public static IReadOnlyList<E2eRow> BuildRows(E2eSummary s) => s.Results.Select((r, i) => new E2eRow(
        i + 1, r.TimestampSeconds.ToString("F6", CultureInfo.InvariantCulture), StatusText(r.Status),
        r.Counter < 0 ? "" : r.Counter.ToString(CultureInfo.InvariantCulture),
        r.Status == E2eStatus.WrongLength ? "" : "0x" + r.ReceivedCrc.ToString("X2", CultureInfo.InvariantCulture),
        r.Status == E2eStatus.WrongLength ? "" : "0x" + r.ExpectedCrc.ToString("X2", CultureInfo.InvariantCulture),
        r.LostFrames, r.GapSeconds is double g ? (g * 1000).ToString("F1", CultureInfo.InvariantCulture) : "",
        r.Status != E2eStatus.Ok || r.TimeoutBefore)).ToList();

    public static string StatusText(E2eStatus s) => s switch
    {
        E2eStatus.Ok => "OK",
        E2eStatus.OkSomeLost => "OK (some frames lost)",
        E2eStatus.WrongCrc => "Wrong CRC",
        E2eStatus.WrongSequence => "Wrong sequence",
        E2eStatus.Repeated => "Repeated counter",
        E2eStatus.WrongLength => "Frame too short",
        _ => "Invalid counter (15)"
    };

    public static E2eOutcome Summarize(E2eSummary s)
    {
        if (s.FrameCount == 0)
            return new E2eOutcome(E2eChip.Amber, "No frames to check",
                "The selected ID has no data frames in the loaded trace.");
        var hard = s.WrongCrc + s.WrongSequence + s.OtherErrors + s.Repeated;
        var soft = s.OkSomeLost + s.TimeoutCount;
        var sb = new StringBuilder();
        sb.AppendLine($"Checked {s.FrameCount:N0} frames: {s.Ok:N0} OK, {s.OkSomeLost:N0} OK with lost frames, " +
                      $"{s.WrongCrc:N0} wrong CRC, {s.WrongSequence:N0} wrong sequence, {s.Repeated:N0} repeated counter, " +
                      $"{s.OtherErrors:N0} short or invalid, {s.TimeoutCount:N0} timeouts.");
        if (s.FirstFailingFrameIndex is int ff)
            sb.AppendLine($"First failing frame: message {ff + 1} at {s.FirstFailingTimestamp?.ToString("F6", CultureInfo.InvariantCulture)} s.");
        sb.Append($"Longest run of consecutive OK frames: {s.LongestOkRun:N0}.");
        if (hard > 0)
            return new E2eOutcome(E2eChip.Red, $"Problems found: {hard:N0} of {s.FrameCount:N0} frames failed", sb.ToString());
        if (soft > 0)
            return new E2eOutcome(E2eChip.Amber, $"Passed with warnings: {soft:N0} event(s) to review", sb.ToString());
        return new E2eOutcome(E2eChip.Green, $"All {s.FrameCount:N0} frames passed", sb.ToString());
    }

    /// <summary>Quoted CSV field with spreadsheet formula neutralisation.</summary>
    public static string CsvEscape(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public static string ToCsv(IEnumerable<E2eRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[] { "Message", "Time (s)", "Status", "Counter", "Received CRC", "Expected CRC", "Lost frames", "Gap (ms)" }.Select(CsvEscape)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", new[]
            {
                r.Message.ToString(CultureInfo.InvariantCulture), r.Time, r.Status, r.Counter, r.ReceivedCrc,
                r.ExpectedCrc, r.Lost.ToString(CultureInfo.InvariantCulture), r.GapMs
            }.Select(CsvEscape)));
        return sb.ToString();
    }
}
