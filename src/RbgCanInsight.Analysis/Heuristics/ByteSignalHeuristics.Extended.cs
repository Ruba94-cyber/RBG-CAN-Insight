namespace RbgCanInsight.Analysis.Heuristics;

// Additional evidence-only detectors: Motorola-order counters, stepped counters, range checksums and
// 16-bit CRCs. Same guards as the base detectors (>= 16 frames, >= 99% checksum fit, >= 95% counter fit);
// everything is a CANDIDATE.
public static partial class ByteSignalHeuristics
{
    private static int MotorolaField(byte[] p, int start, int len)
    {
        int by = start >> 3, bi = start & 7, v = 0;
        for (var i = 0; i < len; i++)
        {
            v = (v << 1) | ((p[by] >> bi) & 1);
            if (bi == 0) { by++; bi = 7; } else bi--;
        }
        return v;
    }

    /// <summary>Bit positions (byte*8 + bit-in-byte) covered by a Motorola (DBC @0) field.</summary>
    private static List<int> MotorolaBits(int start, int len)
    {
        var list = new List<int>(len);
        int by = start >> 3, bi = start & 7;
        for (var i = 0; i < len; i++)
        {
            list.Add(by * 8 + bi);
            if (bi == 0) { by++; bi = 7; } else bi--;
        }
        return list;
    }

    private static IEnumerable<int> CoveredBytes(CounterCandidate c) =>
        c.MotorolaOrder
            ? MotorolaBits(c.StartBit, c.BitLength).Select(b => b >> 3).Distinct()
            : Enumerable.Range(c.StartBit >> 3, ((c.StartBit + c.BitLength - 1) >> 3) - (c.StartBit >> 3) + 1);

    /// <summary>Byte-spanning big-endian counters (+1). The field's most significant bit must toggle:
    /// otherwise it is a shorter counter padded with neighbouring constant bits (the shorter field is
    /// tested on its own). Fields inside a single byte are covered by the Intel detector.</summary>
    private static List<CounterCandidate> FindMotorolaCounters(List<byte[]> rows, int length)
    {
        var found = new List<(CounterCandidate C, HashSet<int> Bits)>();
        if (rows.Count - 1 < MinTransitions) return [];
        var transitions = rows.Count - 1;
        var allowedBad = (int)Math.Floor(transitions * (1 - CounterMinConsistency));
        for (var start = 0; start < length * 8; start++)
        {
            var firstBits = (start & 7) + 1;
            for (var len = firstBits + 1; len <= 16; len++)
            {
                var bits = MotorolaBits(start, len);
                if ((bits[^1] >> 3) >= length) break;
                var mod = 1 << len;
                var bad = 0;
                var seen = new HashSet<int>();
                var prev = MotorolaField(rows[0], start, len);
                seen.Add(prev);
                for (var r = 1; r < rows.Count && bad <= allowedBad; r++)
                {
                    var cur = MotorolaField(rows[r], start, len);
                    seen.Add(cur);
                    if (cur != ((prev + 1) & (mod - 1))) bad++;
                    prev = cur;
                }
                if (bad > allowedBad) continue;
                var msbValues = seen.Select(v => (v >> (len - 1)) & 1).Distinct().Count();
                if (msbValues < 2 || seen.Count < 4) continue;
                found.Add((new CounterCandidate(start, len, mod, transitions - bad, transitions, seen.Count,
                    MotorolaOrder: true), bits.ToHashSet()));
            }
        }
        return found
            .Where(a => !found.Any(b => b.C.BitLength > a.C.BitLength && a.Bits.IsSubsetOf(b.Bits)))
            .Select(x => x.C).OrderBy(c => c.StartBit).ThenBy(c => c.BitLength).ToList();
    }

    private static readonly int[] CounterSteps = [2, 3, 4];

    /// <summary>Intel-order counters with a constant step of 2, 3 or 4. Even steps leave the low bit(s)
    /// constant, so they are accepted only for fields starting on a byte boundary (otherwise a +1 counter next
    /// to a constant bit would be mislabelled as +2). The matching +1 sub-field, if any, is still reported
    /// by the base detector.</summary>
    private static List<CounterCandidate> FindSteppedCounters(List<byte[]> rows, int length)
    {
        var found = new List<CounterCandidate>();
        if (rows.Count - 1 < MinTransitions) return found;
        var transitions = rows.Count - 1;
        var allowedBad = (int)Math.Floor(transitions * (1 - CounterMinConsistency));
        foreach (var step in CounterSteps)
            for (var start = 0; start < length * 8; start++)
            {
                if (step % 2 == 0 && start % 8 != 0) continue;
                for (var len = 4; len <= 8 && start + len <= length * 8; len++)
                {
                    var mod = 1 << len;
                    if (mod < step * 4) continue;
                    var bad = 0;
                    var seen = new HashSet<int>();
                    var prev = Field(rows[0], start, len);
                    seen.Add(prev);
                    for (var r = 1; r < rows.Count && bad <= allowedBad; r++)
                    {
                        var cur = Field(rows[r], start, len);
                        seen.Add(cur);
                        if (cur != ((prev + step) & (mod - 1))) bad++;
                        prev = cur;
                    }
                    if (bad > allowedBad || seen.Count < 4) continue;
                    found.Add(new CounterCandidate(start, len, mod, transitions - bad, transitions, seen.Count,
                        Step: step));
                }
            }
        return found
            .Where(a => !found.Any(b => b.Step == a.Step && b.StartBit <= a.StartBit &&
                a.StartBit + a.BitLength <= b.StartBit + b.BitLength && b.BitLength > a.BitLength))
            .OrderBy(c => c.Step).ThenBy(c => c.StartBit).ThenBy(c => c.BitLength).ToList();
    }

    private static readonly (string Name, Func<byte[], byte> Fn)[] Algs8 =
    [
        ("Xor", b => b.Aggregate((byte)0, (x, y) => (byte)(x ^ y))),
        ("Sum8", b => b.Aggregate((byte)0, (x, y) => (byte)(x + y))),
        ("TwosComplementSum8", b => (byte)-b.Aggregate((byte)0, (x, y) => (byte)(x + y))),
        ("Crc8_J1850_1D", b => Crc8(0x1D, 0xFF, 0xFF, b)),
        ("Crc8_AutosarE2E_2F", b => Crc8(0x2F, 0xFF, 0xFF, b)),
        ("Crc8_07", b => Crc8(0x07, 0x00, 0x00, b)),
    ];

    private static byte[] Pick(byte[] row, int[] indices)
    {
        var o = new byte[indices.Length];
        for (var i = 0; i < indices.Length; i++) o[i] = row[indices[i]];
        return o;
    }

    /// <summary>Checksums over a byte subset: the prefix 0..t-1 for a checksum byte t that is not last, and
    /// "all other bytes except one counter byte" (counter bytes come from the detected counters).</summary>
    private static void AddRangeChecksums(List<byte[]> rows, int length, IReadOnlyList<CounterCandidate> counters,
        bool[] varying, List<ChecksumCandidate> found)
    {
        var counterBytes = counters.SelectMany(CoveredBytes).Where(b => b < length && varying[b]).Distinct().ToList();
        for (var t = 0; t < length; t++)
        {
            var distinct = rows.Select(r => r[t]).Distinct().Count();
            if (distinct < 2) continue;
            var inputs = new List<(string Label, int[] Idx)>();
            if (t >= 1 && t < length - 1)
                inputs.Add(($"bytes 0..{t - 1}", Enumerable.Range(0, t).ToArray()));
            foreach (var cb in counterBytes)
            {
                if (cb == t) continue;
                inputs.Add(($"all bytes except counter byte {cb}",
                    Enumerable.Range(0, length).Where(i => i != t && i != cb).ToArray()));
                if (t >= 2 && t < length - 1 && cb < t)
                    inputs.Add(($"bytes 0..{t - 1} except counter byte {cb}",
                        Enumerable.Range(0, t).Where(i => i != cb).ToArray()));
            }
            foreach (var (label, idx) in inputs)
            {
                if (idx.Length == 0) continue;
                var vary = idx.Count(i => varying[i]);
                if (vary == 0) continue;
                foreach (var (name, fn) in Algs8)
                {
                    var ok = 0;
                    foreach (var row in rows) if (fn(Pick(row, idx)) == row[t]) ok++;
                    if ((double)ok / rows.Count >= ChecksumMinFit)
                        found.Add(new ChecksumCandidate(t, $"{name}({label})", ok, rows.Count, vary, distinct));
                }
            }
        }
    }

    /// <summary>16-bit CRC stored in the last two bytes (either byte order) over bytes 0..n-3, optionally
    /// excluding one counter byte.</summary>
    private static void AddCrc16Checksums(List<byte[]> rows, int length, IReadOnlyList<CounterCandidate> counters,
        bool[] varying, List<ChecksumCandidate> found)
    {
        if (length < 4 || rows.Count < MinTransitions) return;
        var hi = length - 2;
        var counterBytes = counters.SelectMany(CoveredBytes).Where(b => b < hi && varying[b]).Distinct().ToList();
        var inputs = new List<(string Label, int[] Idx)> { ("", Enumerable.Range(0, hi).ToArray()) };
        foreach (var cb in counterBytes)
            inputs.Add(($", excluding counter byte {cb}", Enumerable.Range(0, hi).Where(i => i != cb).ToArray()));
        (string Name, Func<byte[], ushort> Fn)[] algs =
        [
            ("Crc16_CcittFalse_1021_FFFF", b => Crc16CcittFalse(b)),
            ("Crc16_Ibm_8005_Reflected", b => Crc16Ibm(b)),
        ];
        foreach (var bigEndian in new[] { true, false })
        {
            int Target(byte[] r) => bigEndian ? r[hi] << 8 | r[hi + 1] : r[hi + 1] << 8 | r[hi];
            if (rows.Select(Target).Distinct().Count() < 2) continue;
            var distinct = rows.Select(Target).Distinct().Count();
            foreach (var (label, idx) in inputs)
            {
                if (idx.Length == 0) continue;
                var vary = idx.Count(i => varying[i]);
                if (vary == 0) continue;
                foreach (var (name, fn) in algs)
                {
                    var ok = 0;
                    foreach (var row in rows) if (fn(Pick(row, idx)) == Target(row)) ok++;
                    if ((double)ok / rows.Count >= ChecksumMinFit)
                        found.Add(new ChecksumCandidate(hi, $"{name}({(bigEndian ? "big" : "little")}-endian{label})",
                            ok, rows.Count, vary, distinct, ByteCount: 2));
                }
            }
        }
    }

    /// <summary>CRC-16/CCITT-FALSE: poly 0x1021, init 0xFFFF, no reflection, xorout 0 (check "123456789" = 0x29B1).</summary>
    public static ushort Crc16CcittFalse(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>CRC-16/IBM (ARC): poly 0x8005 reflected (0xA001), init 0, xorout 0 (check "123456789" = 0xBB3D).</summary>
    public static ushort Crc16Ibm(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}
