using System.Globalization;

namespace RbgCanInsight.DiagData;

public enum MappingDiffKind
{
    Unchanged,
    Changed,
    Added,
    Removed,
    Ambiguous,
    Unverifiable
}

public enum MappingDefinitionEvidence
{
    Same,
    Changed,
    Unverifiable,
    NotApplicable
}

/// <summary>One conservative comparison of two saved DID/CAN associations.
/// Baseline/current objects retain the original notes and identities for export.
/// </summary>
public sealed record MappingDiffRow(
    int Did,
    string Ecu,
    MappingDiffKind Kind,
    DidSignalMapping? Baseline,
    DidSignalMapping? Current,
    MappingDefinitionEvidence DidDefinition,
    MappingDefinitionEvidence CanDefinition,
    bool RequiresReview,
    string Explanation,
    int BaselineCount = 1,
    int CurrentCount = 1)
{
    public string DidHex => $"0x{Did:X4}";
    public string BaselineSignal => MappingDocumentComparer.Target(Baseline);
    public string CurrentSignal => MappingDocumentComparer.Target(Current);
}

public sealed record MappingComparisonReport(IReadOnlyList<MappingDiffRow> Rows)
{
    public int Added => Rows.Count(row => row.Kind == MappingDiffKind.Added);
    public int Removed => Rows.Count(row => row.Kind == MappingDiffKind.Removed);
    public int Changed => Rows.Count(row => row.Kind == MappingDiffKind.Changed);
    public int Ambiguous => Rows.Count(row => row.Kind == MappingDiffKind.Ambiguous);
    public int Unverifiable => Rows.Count(row => row.Kind == MappingDiffKind.Unverifiable);
    public int Unchanged => Rows.Count(row => row.Kind == MappingDiffKind.Unchanged);
    public int RequiresReview => Rows.Count(row => row.RequiresReview);
    public string Summary =>
        $"{Rows.Count:N0} identities: {Added:N0} added, {Removed:N0} removed, " +
        $"{Changed:N0} changed, {Ambiguous:N0} ambiguous, {Unverifiable:N0} unverifiable, " +
        $"{RequiresReview:N0} requiring review";
}

/// <summary>
/// Read-only comparison of two .rbgmap.json snapshots. DID+ECU is the coarse
/// collision bucket; if either map contains several variants within it, do not
/// silently pair them by insertion order, name or hash. Expose one ambiguous
/// group so the analyst can select authoritative ECU/variant identity.
/// </summary>
public static class MappingDocumentComparer
{
    public static MappingComparisonReport Compare(
        MappingDocument baseline, MappingDocument current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        var previous = Index(baseline.Mappings);
        var latest = Index(current.Mappings);
        var keys = previous.Keys.Concat(latest.Keys).Distinct()
            .OrderBy(key => key.Did).ThenBy(key => key.Ecu, StringComparer.Ordinal).ToArray();
        var rows = new List<MappingDiffRow>(keys.Length);
        foreach (var key in keys)
        {
            previous.TryGetValue(key, out var beforeGroup);
            latest.TryGetValue(key, out var afterGroup);
            var beforeCount = beforeGroup?.Count ?? 0;
            var afterCount = afterGroup?.Count ?? 0;
            var before = beforeCount == 1 ? beforeGroup![0] : null;
            var after = afterCount == 1 ? afterGroup![0] : null;
            var ecu = after?.Ecu ?? before?.Ecu ?? key.Ecu;

            if (beforeCount > 1 || afterCount > 1)
            {
                rows.Add(new MappingDiffRow(key.Did, ecu, MappingDiffKind.Ambiguous,
                    before, after, MappingDefinitionEvidence.Unverifiable,
                    MappingDefinitionEvidence.Unverifiable, true,
                    $"Duplicate DID/ECU identities: baseline {beforeCount}, current {afterCount}. " +
                    "No variant matching was guessed.", beforeCount, afterCount));
                continue;
            }
            if (before is null || after is null)
            {
                var added = before is null;
                rows.Add(new MappingDiffRow(key.Did, ecu,
                    added ? MappingDiffKind.Added : MappingDiffKind.Removed,
                    before, after, MappingDefinitionEvidence.NotApplicable,
                    MappingDefinitionEvidence.NotApplicable, false,
                    added ? "Association added in current map." : "Association removed from current map.",
                    beforeCount, afterCount));
                continue;
            }

            var beforeMapped = !string.IsNullOrWhiteSpace(before.Signal);
            var afterMapped = !string.IsNullOrWhiteSpace(after.Signal);
            var didEvidence = CompareFingerprint(
                before.DidDefinitionFingerprint, after.DidDefinitionFingerprint);
            // The map stores all DIDs, including rows without any CAN
            // association. No CAN fingerprint is applicable if both are
            // unmapped; those are not false "unverified signal identities".
            var canEvidence = !beforeMapped && !afterMapped
                ? MappingDefinitionEvidence.NotApplicable
                : CompareFingerprint(before.CanSignalFingerprint, after.CanSignalFingerprint);
            var identityChanged = !string.Equals(before.DidName, after.DidName, StringComparison.Ordinal) ||
                                  !SameTarget(before, after);
            var statusChanged = before.Status != after.Status;
            var notesChanged = !string.Equals(before.Notes, after.Notes, StringComparison.Ordinal);
            var scoreChanged = !before.Confidence.Equals(after.Confidence);
            var explanationChanged = !string.Equals(before.Reason, after.Reason, StringComparison.Ordinal);
            var definitionChanged = didEvidence == MappingDefinitionEvidence.Changed ||
                                    canEvidence == MappingDefinitionEvidence.Changed;
            var evidenceUnverifiable = didEvidence == MappingDefinitionEvidence.Unverifiable ||
                                       canEvidence == MappingDefinitionEvidence.Unverifiable;
            var detail = new List<string>();
            if (!string.Equals(before.DidName, after.DidName, StringComparison.Ordinal))
                detail.Add("DID name changed");
            if (!SameTarget(before, after)) detail.Add("CAN signal identity changed");
            if (didEvidence == MappingDefinitionEvidence.Changed)
                detail.Add("DID definition fingerprint changed");
            if (canEvidence == MappingDefinitionEvidence.Changed)
                detail.Add("DBC signal fingerprint changed");
            if (evidenceUnverifiable)
                detail.Add("Missing or unsupported versioned fingerprints: semantic equality cannot be established");
            if (statusChanged) detail.Add($"Review status {before.Status} → {after.Status}");
            if (notesChanged) detail.Add("Analyst notes changed");
            if (scoreChanged) detail.Add("Candidate score changed");
            if (explanationChanged) detail.Add("Ranking explanation changed");

            var dataChanged = identityChanged || statusChanged || notesChanged ||
                              scoreChanged || explanationChanged || definitionChanged;
            var kind = dataChanged ? MappingDiffKind.Changed :
                evidenceUnverifiable ? MappingDiffKind.Unverifiable : MappingDiffKind.Unchanged;
            // Retaining a Confirmed/Rejected review after a changed/unverifiable
            // definition is unsafe. A transition to ToVerify is already flagged.
            var review = (beforeMapped || afterMapped) &&
                (identityChanged || definitionChanged || evidenceUnverifiable ||
                 (statusChanged && after.Status == MappingStatus.ToVerify));
            rows.Add(new MappingDiffRow(key.Did, ecu, kind, before, after,
                didEvidence, canEvidence, review,
                detail.Count == 0 ? "Same understood definitions and saved association." :
                string.Join("; ", detail), beforeCount, afterCount));
        }
        return new MappingComparisonReport(rows);
    }

    private static Dictionary<(int Did, string Ecu), List<DidSignalMapping>> Index(
        IReadOnlyList<DidSignalMapping> mappings)
    {
        var index = new Dictionary<(int Did, string Ecu), List<DidSignalMapping>>();
        foreach (var item in mappings)
        {
            var key = (item.Did, (item.Ecu ?? string.Empty).Trim().ToUpperInvariant());
            if (!index.TryGetValue(key, out var group))
                index[key] = group = [];
            group.Add(item);
        }
        return index;
    }

    public static string Target(DidSignalMapping? mapping)
    {
        if (mapping is null) return string.Empty;
        if (string.IsNullOrWhiteSpace(mapping.Signal)) return "(unmapped)";
        var id = mapping.MessageIdentifier is uint arbitration
            ? "0x" + arbitration.ToString("X", CultureInfo.InvariantCulture) : "ID unknown";
        var format = mapping.IsExtended is true ? "29-bit" :
            mapping.IsExtended is false ? "11-bit" : "format unknown";
        return $"{mapping.Database}/{mapping.Message} [{id}, {format}] / {mapping.Signal}";
    }

    private static bool SameTarget(DidSignalMapping a, DidSignalMapping b) =>
        string.Equals(a.Database, b.Database, StringComparison.Ordinal) &&
        string.Equals(a.Message, b.Message, StringComparison.Ordinal) &&
        string.Equals(a.Signal, b.Signal, StringComparison.Ordinal) &&
        a.MessageIdentifier == b.MessageIdentifier &&
        a.IsExtended == b.IsExtended;

    // A source file may contain arbitrary strings named "fingerprints".
    // Only the current semantic-hash format supports a meaningful comparison:
    // unknown future revisions and malformed legacy values are unverifiable,
    // even when the same invalid string occurs on both sides.
    private static MappingDefinitionEvidence CompareFingerprint(string? before, string? after)
    {
        if (!IsCurrentFingerprint(before) || !IsCurrentFingerprint(after))
            return MappingDefinitionEvidence.Unverifiable;
        return string.Equals(before, after, StringComparison.Ordinal)
            ? MappingDefinitionEvidence.Same : MappingDefinitionEvidence.Changed;
    }

    private static bool IsCurrentFingerprint(string? value)
    {
        if (value is null || value.Length != 71 ||
            !value.StartsWith("sem-v1:", StringComparison.Ordinal))
            return false;
        foreach (var c in value.AsSpan(7))
        {
            if (!(c is >= '0' and <= '9' or >= 'A' and <= 'F'))
                return false;
        }
        return true;
    }
}
