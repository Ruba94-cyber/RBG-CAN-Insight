namespace RbgCanInsight.Diagnostics;

/// <summary>Explains the selected observed DTC status byte, without treating a
/// positive 0x14 clear as a later 0x19 read-back from the ECU.</summary>
public static class DtcStatusEvidence
{
    public static string Explain(IReadOnlyList<DtcStatusTransition> timeline,
        DtcStatusTransition? selected)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (selected is null) return "Select a recorded DTC event to inspect its eight ISO 14229 status bits.";

        var index = -1;
        for (var i = 0; i < timeline.Count; i++)
        {
            if (ReferenceEquals(timeline[i], selected)) { index = i; break; }
        }
        if (index < 0) return "This DTC event is no longer in the current evidence snapshot.";

        if (selected.Operation == "ClearDiagnosticInformation")
            return "UDS 0x14 clear operation acknowledged. The 'cleared' status in this history is an analysis marker, NOT a 0x19 status read back from the ECU. ReadDTCInformation 0x19 is needed for post-clear verification.";

        if (selected.Status is not { } current)
            return "No recorded status byte for this event; inactive, pending or confirmed state cannot be inferred.";

        byte? previous = null;
        var afterClear = false;
        for (var i = index - 1; i >= 0; i--)
        {
            var earlier = timeline[i];
            if (earlier.Operation == "ClearDiagnosticInformation")
            {
                afterClear = true;
                break;
            }
            if (earlier.Status is { } known)
            {
                previous = known;
                break;
            }
        }

        var active = DtcStatusBits.Names.Where((_, bit) =>
            DtcStatusBits.IsSet(current, bit)).ToArray();
        var text = $"Recorded status 0x{current:X2}. Set bits: " +
            (active.Length == 0 ? "none" : string.Join(", ", active)) + ". ";
        if (previous is not { } before)
        {
            text += afterClear
                ? "First status observation after an acknowledged 0x14 clear; earlier status bits cannot be used as a before/after comparison."
                : "First comparable status observation; no preceding status byte was recorded.";
        }
        else
        {
            var set = DtcStatusBits.Names.Where((_, bit) =>
                !DtcStatusBits.IsSet(before, bit) &&
                DtcStatusBits.IsSet(current, bit)).ToArray();
            var reset = DtcStatusBits.Names.Where((_, bit) =>
                DtcStatusBits.IsSet(before, bit) &&
                !DtcStatusBits.IsSet(current, bit)).ToArray();
            text += $"Compared with the preceding recorded status 0x{before:X2}: " +
                (set.Length == 0 ? "no newly set bits" : "set " + string.Join(", ", set)) +
                "; " +
                (reset.Length == 0 ? "no reset bits" : "reset " + string.Join(", ", reset)) + ".";
        }
        return text +
            " Status bits reflect observed ECU test state; a zero bit does not prove DTC erasure.";
    }
}
