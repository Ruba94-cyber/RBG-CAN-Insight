using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Buses;

/// <summary>Per (channel, FlexRay channel, slot) evidence. Indices refer to the FlexRay frame list.</summary>
public sealed record FlexRaySlotRow(
    int Channel, FlexRayChannel FlexRayChannel, int Slot, string Segment, int Frames, int NullFrames, int ErrorFrames,
    bool Sync, bool Startup, IReadOnlyList<int> PayloadLengths, IReadOnlyList<int> SeenCycles, int FramesWithoutCycle,
    int InferredRepetition, int InferredBaseCycle, IReadOnlyList<string> MissingInstances,
    int FirstIndex, int LastIndex, int FirstErrorIndex);

public sealed record FlexRayMatrixResult(IReadOnlyList<FlexRaySlotRow> Rows, int CycleInstances);

/// <summary>Slot / cycle matrix with missing-slot and recorded-error evidence. The communication schedule is NOT
/// known: for static slots the repetition is inferred as the largest power of two (up to 64) consistent with all
/// cycles in which the slot was seen, so a slot that is absent in every one of its cycles is never reported.
/// Dynamic-segment slots are never reported as missing. Header and frame CRCs are not recomputed: "errors" are
/// frames the recorder itself flagged as erroneous.</summary>
public static class FlexRayAnalyzer
{
    private sealed class Slot
    {
        public int Frames, Nulls, Errors, NoCycle, First = -1, Last, FirstError = -1;
        public bool Sync, Startup, Dynamic;
        public SortedSet<int> Lengths = [];
        public SortedSet<int> Cycles = [];
        public List<int> Instances = [];
    }

    public static FlexRayMatrixResult Analyze(IReadOnlyList<FlexRayFrame> frames)
    {
        var slots = new Dictionary<(int, FlexRayChannel, int), Slot>();
        // per application channel: cycle number and first frame index of each observed cycle instance
        var instCycle = new Dictionary<int, List<byte>>();
        var instStart = new Dictionary<int, List<int>>();
        var order = Enumerable.Range(0, frames.Count).OrderBy(i => frames[i].TimestampNanoseconds).ToArray();

        foreach (var i in order)
        {
            var f = frames[i];
            var key = (f.Channel, f.FlexRayChannel, f.Slot);
            if (!slots.TryGetValue(key, out var s)) slots[key] = s = new Slot { First = i };
            s.Frames++; s.Last = Math.Max(s.Last, i);
            if (f.IsNull) s.Nulls++;
            if (f.IsError) { s.Errors++; if (s.FirstError < 0) s.FirstError = i; }
            if ((f.Flags & FlexRayFlags.Sync) != 0) s.Sync = true;
            if ((f.Flags & FlexRayFlags.Startup) != 0) s.Startup = true;
            if (f.IsDynamic == true) s.Dynamic = true;
            s.Lengths.Add(f.DeclaredPayloadBytes);
            if (f.Cycle < 0) { s.NoCycle++; continue; }
            s.Cycles.Add(f.Cycle);

            if (!instCycle.TryGetValue(f.Channel, out var cyc)) { instCycle[f.Channel] = cyc = []; instStart[f.Channel] = []; }
            if (cyc.Count == 0 || cyc[^1] != f.Cycle) { cyc.Add((byte)f.Cycle); instStart[f.Channel].Add(i); }
            var inst = cyc.Count - 1;
            if (s.Instances.Count == 0 || s.Instances[^1] != inst) s.Instances.Add(inst);
        }

        var rows = new List<FlexRaySlotRow>();
        foreach (var (key, s) in slots.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item3).ThenBy(p => p.Key.Item2))
        {
            var rep = 0; var baseCycle = 0;
            var missing = new List<string>();
            if (s.Cycles.Count > 0)
            {
                foreach (var r in new[] { 64, 32, 16, 8, 4, 2, 1 })
                {
                    var b = s.Cycles.Min % r;
                    if (s.Cycles.All(c => c % r == b)) { rep = r; baseCycle = b; break; }
                }
                if (!s.Dynamic && instCycle.TryGetValue(key.Item1, out var cyc) && s.Instances.Count > 0)
                {
                    var seen = new HashSet<int>(s.Instances);
                    for (var inst = s.Instances[0]; inst <= s.Instances[^1]; inst++)
                        if (cyc[inst] % rep == baseCycle && !seen.Contains(inst))
                            missing.Add($"cycle {cyc[inst]} near frame #{instStart[key.Item1][inst]}");
                }
            }
            rows.Add(new FlexRaySlotRow(key.Item1, key.Item2, key.Item3,
                s.Dynamic ? "dynamic" : frames[s.First].IsDynamic == false ? "static" : "unknown",
                s.Frames, s.Nulls, s.Errors, s.Sync, s.Startup, s.Lengths.ToArray(), s.Cycles.ToArray(), s.NoCycle,
                rep, baseCycle, missing, s.First, s.Last, s.FirstError));
        }
        return new FlexRayMatrixResult(rows, instCycle.Values.Sum(l => l.Count));
    }

    public static string CompressRanges(IEnumerable<int> values)
    {
        var list = values.Distinct().OrderBy(v => v).ToList();
        var parts = new List<string>();
        for (var i = 0; i < list.Count;)
        {
            var j = i;
            while (j + 1 < list.Count && list[j + 1] == list[j] + 1) j++;
            parts.Add(j - i >= 2 ? $"{list[i]}-{list[j]}" : string.Join(",", list.Skip(i).Take(j - i + 1)));
            i = j + 1;
        }
        return string.Join(",", parts);
    }
}
