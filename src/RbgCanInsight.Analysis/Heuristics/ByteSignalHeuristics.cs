using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Heuristics;

/// <summary>A bit field that behaved like a counter. Default (MotorolaOrder=false, Step=1): increment by 1;
/// payload bit i = byte i/8, bit i%8 (LSB-first); the field is read least-significant-bit first from StartBit
/// (Intel-style). With MotorolaOrder=true StartBit is the DBC @0 start bit (byte*8 + bit-in-byte, bit 7 = MSB of
/// the byte) of the field's MOST significant bit; the field continues towards bit 0 and then at bit 7 of the next
/// byte. Step is the constant increment (1, or 2..4 for stepped counters). A CANDIDATE, not a signal definition.</summary>
public sealed record CounterCandidate(
    int StartBit, int BitLength, int Modulus,
    int ConsistentTransitions, int TotalTransitions, int DistinctValues,
    bool MotorolaOrder = false, int Step = 1)
{
    public const string Label = "candidate";
    public double Confidence => TotalTransitions == 0 ? 0 : (double)ConsistentTransitions / TotalTransitions;
}

/// <summary>One byte whose value equalled the named algorithm over the other bytes in
/// ConsistentFrames of TotalFrames frames. A CANDIDATE: coincidence is possible when few other
/// bytes vary (see VaryingInputBytes).</summary>
public sealed record ChecksumCandidate(
    int ByteIndex, string Algorithm, int ConsistentFrames, int TotalFrames,
    int VaryingInputBytes, int TargetDistinctValues, int ByteCount = 1)
{
    public const string Label = "candidate";
    public double Fit => TotalFrames == 0 ? 0 : (double)ConsistentFrames / TotalFrames;
}

public sealed record ByteSignalAnalysis(
    int Channel, uint Identifier, bool IsExtended,
    int FrameCount, int AnalyzedFrames, int PayloadLength,
    IReadOnlyList<CounterCandidate> Counters,
    IReadOnlyList<ChecksumCandidate> Checksums,
    double[] BitToggleRates, double[] ByteEntropyBits,
    byte[] ConstantBitMask, byte[] ConstantBitValues)
{
    public int ConstantBitCount => ConstantBitMask.Sum(b => System.Numerics.BitOperations.PopCount(b));
}

/// <summary>
/// Evidence-only analysis of one (channel, id, format) payload series, in the order supplied
/// (time order is the caller's responsibility). Only frames with the most common payload length
/// are analysed; the others are counted in FrameCount - AnalyzedFrames.
/// Counters: increment by 1 modulo 2^n, n=2..8, every start bit; kept at >= 95% of transitions
/// and >= 16 transitions; a field wholly contained in a larger passing field is dropped.
/// Checksums: for every byte t and algorithm, compared against the algorithm over all other bytes in
/// index order; kept at >= 99% and when the target byte takes >= 2 values and >= 16 frames.
/// Also (see the Extended file): Motorola-order byte-spanning counters, +2..+4 stepped counters, range checksums
/// (prefix 0..t-1, or all bytes except one counter byte) and CRC-16/CCITT-FALSE and CRC-16/IBM in the last two bytes.
/// Algorithms: Xor, Sum8, TwosComplementSum8, XorExcludingCounterByteWithNibble /
/// XorAllWithCounterNibbleCancelled (for 4-bit byte-aligned nibble counters), Crc8_J1850_1D,
/// Crc8_AutosarE2E_2F, Crc8_07.
/// </summary>
public static partial class ByteSignalHeuristics
{
    public const double CounterMinConsistency = 0.95;
    public const double ChecksumMinFit = 0.99;
    public const int MinTransitions = 16;

    public static IReadOnlyList<ByteSignalAnalysis> AnalyzeFrames(
        IEnumerable<CanFrame> frames, Func<int, uint, bool, bool>? includeIdentifier = null)
    {
        var groups = new Dictionary<(int, uint, bool), List<byte[]>>();
        foreach (var f in frames)
        {
            if (f.IsError || f.Flags.HasFlag(CanFrameFlags.Remote)) continue;
            if (includeIdentifier is not null && !includeIdentifier(f.Channel, f.Identifier, f.IsExtended)) continue;
            var key = (f.Channel, f.Identifier, f.IsExtended);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(f.GetPayload());
        }
        return groups.OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2).ThenBy(g => g.Key.Item3)
            .Select(g => Analyze(g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Value)).ToList();
    }

    public static ByteSignalAnalysis Analyze(
        int channel, uint identifier, bool isExtended, IReadOnlyList<byte[]> payloads)
    {
        var total = payloads.Count;
        var length = total == 0 ? 0 : payloads.GroupBy(p => p.Length)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        var rows = payloads.Where(p => p.Length == length).ToList();
        var n = rows.Count;
        var counters = length == 0 ? [] : FindCounters(rows, length)
            .Concat(FindMotorolaCounters(rows, length)).Concat(FindSteppedCounters(rows, length)).ToList();
        var checksums = length < 2 ? [] : FindChecksums(rows, length, counters);

        var toggles = new double[length * 8];
        for (var bit = 0; bit < toggles.Length && n > 1; bit++)
        {
            var t = 0;
            for (var r = 1; r < n; r++)
                if (Bit(rows[r], bit) != Bit(rows[r - 1], bit)) t++;
            toggles[bit] = (double)t / (n - 1);
        }
        var entropy = new double[length];
        for (var b = 0; b < length && n > 0; b++)
        {
            var hist = new int[256];
            foreach (var row in rows) hist[row[b]]++;
            double h = 0;
            foreach (var c in hist)
            {
                if (c == 0) continue;
                var p = (double)c / n;
                h -= p * Math.Log2(p);
            }
            entropy[b] = h == 0 ? 0 : h; // avoid -0
        }
        var mask = new byte[length];
        var values = new byte[length];
        if (n > 0)
            for (var b = 0; b < length; b++)
            {
                var first = rows[0][b];
                byte changed = 0;
                foreach (var row in rows) changed |= (byte)(row[b] ^ first);
                mask[b] = (byte)~changed;
                values[b] = (byte)(first & mask[b]);
            }
        return new ByteSignalAnalysis(channel, identifier, isExtended, total, n, length,
            counters, checksums, toggles, entropy, mask, values);
    }

    private static int Bit(byte[] p, int bit) => (p[bit >> 3] >> (bit & 7)) & 1;

    private static int Field(byte[] p, int start, int len)
    {
        var v = 0;
        for (var i = 0; i < len; i++) v |= Bit(p, start + i) << i;
        return v;
    }

    private static List<CounterCandidate> FindCounters(List<byte[]> rows, int length)
    {
        var found = new List<CounterCandidate>();
        if (rows.Count - 1 < MinTransitions) return found;
        for (var start = 0; start < length * 8; start++)
            for (var len = 2; len <= 8 && start + len <= length * 8; len++)
            {
                var mod = 1 << len;
                var ok = 0;
                var seen = new HashSet<int>();
                var prev = Field(rows[0], start, len);
                seen.Add(prev);
                for (var r = 1; r < rows.Count; r++)
                {
                    var cur = Field(rows[r], start, len);
                    seen.Add(cur);
                    if (cur == ((prev + 1) & (mod - 1))) ok++;
                    prev = cur;
                }
                var transitions = rows.Count - 1;
                if ((double)ok / transitions >= CounterMinConsistency)
                    found.Add(new CounterCandidate(start, len, mod, ok, transitions, seen.Count));
            }
        return found
            .Where(a => !found.Any(b => !ReferenceEquals(a, b) &&
                b.StartBit <= a.StartBit && a.StartBit + a.BitLength <= b.StartBit + b.BitLength &&
                b.BitLength > a.BitLength))
            .OrderBy(c => c.StartBit).ThenBy(c => c.BitLength).ToList();
    }

    private static List<ChecksumCandidate> FindChecksums(
        List<byte[]> rows, int length, IReadOnlyList<CounterCandidate> counters)
    {
        var found = new List<ChecksumCandidate>();
        if (rows.Count < MinTransitions) return found;
        var nibbles = counters.Where(c => !c.MotorolaOrder && c.Step == 1 && c.BitLength == 4 && c.StartBit % 4 == 0).ToList();
        var varying = new bool[length];
        for (var b = 0; b < length; b++)
            varying[b] = rows.Any(r => r[b] != rows[0][b]);
        for (var t = 0; t < length; t++)
        {
            var distinct = rows.Select(r => r[t]).Distinct().Count();
            if (distinct < 2) continue;
            var target = t;
            var varyingInputs = Enumerable.Range(0, length).Count(i => i != target && varying[i]);
            void Test(string name, Func<byte[], byte> compute)
            {
                var ok = 0;
                foreach (var row in rows) if (compute(row) == row[target]) ok++;
                if ((double)ok / rows.Count >= ChecksumMinFit)
                    found.Add(new ChecksumCandidate(target, name, ok, rows.Count, varyingInputs, distinct));
            }
            Test("Xor", r => Fold(r, target, -1, (a, b) => (byte)(a ^ b)));
            Test("Sum8", r => Fold(r, target, -1, (a, b) => (byte)(a + b)));
            Test("TwosComplementSum8", r => (byte)(-Fold(r, target, -1, (a, b) => (byte)(a + b))));
            foreach (var c in nibbles)
            {
                var cb = c.StartBit / 8;
                if (cb == target) continue;
                var shift = c.StartBit % 8;
                Test($"XorExcludingCounterByteWithNibble(counterStartBit={c.StartBit})",
                    r => (byte)(Fold(r, target, cb, (a, b) => (byte)(a ^ b)) ^ ((r[cb] >> shift) & 0xF)));
                Test($"XorAllWithCounterNibbleCancelled(counterStartBit={c.StartBit})",
                    r => (byte)(Fold(r, target, -1, (a, b) => (byte)(a ^ b)) ^ ((r[cb] >> shift) & 0xF)));
            }
            Test("Crc8_J1850_1D", r => Crc8(0x1D, 0xFF, 0xFF, Others(r, target)));
            Test("Crc8_AutosarE2E_2F", r => Crc8(0x2F, 0xFF, 0xFF, Others(r, target)));
            Test("Crc8_07", r => Crc8(0x07, 0x00, 0x00, Others(r, target)));
        }
        AddRangeChecksums(rows, length, counters, varying, found);
        AddCrc16Checksums(rows, length, counters, varying, found);
        return found.OrderBy(c => c.ByteIndex).ThenBy(c => c.Algorithm, StringComparer.Ordinal).ToList();
    }

    private static byte Fold(byte[] row, int skipA, int skipB, Func<byte, byte, byte> op)
    {
        byte acc = 0;
        for (var i = 0; i < row.Length; i++)
            if (i != skipA && i != skipB) acc = op(acc, row[i]);
        return acc;
    }

    private static byte[] Others(byte[] row, int skip)
    {
        var o = new byte[row.Length - 1];
        for (int i = 0, j = 0; i < row.Length; i++) if (i != skip) o[j++] = row[i];
        return o;
    }

    /// <summary>Bitwise MSB-first, non-reflected CRC-8.</summary>
    public static byte Crc8(byte polynomial, byte init, byte xorOut, ReadOnlySpan<byte> data)
    {
        var crc = init;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ polynomial) : (byte)(crc << 1);
        }
        return (byte)(crc ^ xorOut);
    }
}
