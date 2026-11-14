using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Buses;

/// <summary>LIN protected identifier and checksum arithmetic (LIN 2.x / ISO 17987-3).</summary>
public static class LinProtocol
{
    public const byte MasterRequestId = 0x3C;
    public const byte SlaveResponseId = 0x3D;

    public static bool IsDiagnosticId(byte frameId) => (frameId & 0x3F) is MasterRequestId or SlaveResponseId;

    /// <summary>PID = ID | P0 &lt;&lt; 6 | P1 &lt;&lt; 7 with P0 = ID0^ID1^ID2^ID4 and P1 = !(ID1^ID3^ID4^ID5).</summary>
    public static byte ProtectedId(byte frameId)
    {
        var id = frameId & 0x3F;
        int Bit(int n) => (id >> n) & 1;
        var p0 = Bit(0) ^ Bit(1) ^ Bit(2) ^ Bit(4);
        var p1 = (Bit(1) ^ Bit(3) ^ Bit(4) ^ Bit(5)) ^ 1;
        return (byte)(id | (p0 << 6) | (p1 << 7));
    }

    public static bool IsValidProtectedId(byte pid) => ProtectedId((byte)(pid & 0x3F)) == pid;

    /// <summary>Inverted sum with the carry folded back, over the data and, for the enhanced model, the PID.</summary>
    public static byte Checksum(ReadOnlySpan<byte> data, byte? pidForEnhanced)
    {
        var sum = (int)(pidForEnhanced ?? 0);
        foreach (var b in data)
        {
            sum += b;
            if (sum > 0xFF) sum -= 0xFF;
        }
        return (byte)~sum;
    }
}

public enum LinPidStatus { NotChecked, Valid, Invalid }

public enum LinChecksumStatus { NoChecksumRecorded, Truncated, ClassicOnly, EnhancedOnly, ClassicAndEnhanced, Mismatch }

/// <summary>Verification result for one LIN record. <see cref="Index"/> is the index in the LIN frame list.</summary>
public sealed record LinFrameCheck(
    int Index, LinFrame Frame, LinPidStatus Pid, LinChecksumStatus Checksum, string Verdict);

public sealed record LinIdTiming(
    int Channel, byte FrameId, int Count, double MinMs, double MeanMs, double MaxMs, int FirstIndex, int LastIndex);

public static class LinFrameAnalyzer
{
    public static IReadOnlyList<LinFrameCheck> Check(IReadOnlyList<LinFrame> frames)
    {
        var result = new List<LinFrameCheck>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f.Event is LinEventKind.Wakeup or LinEventKind.Sleep)
            {
                result.Add(new(i, f, LinPidStatus.NotChecked, LinChecksumStatus.NoChecksumRecorded,
                    f.Event.ToString().ToLowerInvariant() + " event"));
                continue;
            }
            var pid = f.IdIsProtected
                ? LinProtocol.IsValidProtectedId(f.RawIdByte) ? LinPidStatus.Valid : LinPidStatus.Invalid
                : LinPidStatus.NotChecked;
            var checksum = LinChecksumStatus.NoChecksumRecorded;
            var notes = new List<string>();
            if (pid == LinPidStatus.Invalid)
                notes.Add($"PID parity wrong (expected 0x{LinProtocol.ProtectedId(f.FrameId):X2})");
            if (f.Event is LinEventKind.Message or LinEventKind.CrcError && f.Checksum is { } recorded)
            {
                if (f.Data.Length < f.DeclaredLength)
                {
                    checksum = LinChecksumStatus.Truncated;
                    notes.Add("data shorter than declared length");
                }
                else
                {
                    var pidByte = LinProtocol.ProtectedId(f.FrameId);
                    var classicSum = LinProtocol.Checksum(f.Data, null);
                    var enhancedSum = LinProtocol.Checksum(f.Data, pidByte);
                    var classic = classicSum == recorded;
                    var enhanced = enhancedSum == recorded;
                    checksum = classic && enhanced ? LinChecksumStatus.ClassicAndEnhanced
                        : classic ? LinChecksumStatus.ClassicOnly
                        : enhanced ? LinChecksumStatus.EnhancedOnly : LinChecksumStatus.Mismatch;
                    if (checksum == LinChecksumStatus.Mismatch)
                        notes.Add($"checksum 0x{recorded:X2} matches neither classic (0x{classicSum:X2}) nor enhanced (0x{enhancedSum:X2})");
                    if (LinProtocol.IsDiagnosticId(f.FrameId) && checksum == LinChecksumStatus.EnhancedOnly)
                        notes.Add("diagnostic frames 0x3C/0x3D must use the classic checksum");
                }
            }
            if (f.Event == LinEventKind.CrcError) notes.Add("recorded by the logger as a LIN CRC error");
            if (f.Event is LinEventKind.SendError or LinEventKind.ReceiveError) notes.Add($"{f.Event} event: {f.Detail}");
            var verdict = notes.Count > 0 ? string.Join("; ", notes)
                : checksum is LinChecksumStatus.NoChecksumRecorded ? "no checksum recorded" : "ok";
            result.Add(new(i, f, pid, checksum, verdict));
        }
        return result;
    }

    /// <summary>Per (channel, ID) period statistics over recorded message events.</summary>
    public static IReadOnlyList<LinIdTiming> Timing(IReadOnlyList<LinFrame> frames)
    {
        var last = new Dictionary<(int, byte), (long Ts, int Index)>();
        var acc = new Dictionary<(int, byte), (int Deltas, double Min, double Max, double Sum, int First, int Final)>();
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f.Event != LinEventKind.Message) continue;
            var key = (f.Channel, (byte)f.FrameId);
            if (last.TryGetValue(key, out var prev))
            {
                var delta = (f.TimestampNanoseconds - prev.Ts) / 1e6;
                if (delta >= 0)
                {
                    acc.TryGetValue(key, out var a);
                    acc[key] = a.Deltas == 0
                        ? (1, delta, delta, delta, prev.Index, i)
                        : (a.Deltas + 1, Math.Min(a.Min, delta), Math.Max(a.Max, delta), a.Sum + delta, a.First, i);
                }
            }
            last[key] = (f.TimestampNanoseconds, i);
        }
        return acc.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2)
            .Select(p => new LinIdTiming(p.Key.Item1, p.Key.Item2, p.Value.Deltas + 1, p.Value.Min,
                p.Value.Sum / p.Value.Deltas, p.Value.Max, p.Value.First, p.Value.Final)).ToArray();
    }

    public static string Hex(ReadOnlySpan<byte> data) =>
        string.Join(' ', data.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}
