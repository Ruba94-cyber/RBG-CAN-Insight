using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.Correlation;

/// <summary>
/// One recorded DTC snapshot DID field connected to a separate DID↔DBC
/// time-series candidate. This is a navigation lead, NOT a claim that the
/// snapshot value was itself paired with that CAN signal.
/// </summary>
public sealed record DtcSnapshotCanLink(
    DecodedDtcRecordValue SnapshotField,
    UdsCanCorrelationResult Correlation,
    bool SnapshotRecordFullyDecoded = true)
{
    public int Did => SnapshotField.Did ?? -1;
    public string FieldScope => Correlation.ObservedField.Contains('/')
        ? "Recorded field" : "DID only";
    public string EvidenceSummary =>
        $"Snapshot DID 0x{Did:X4}: original CAN bytes verified; " +
        $"{Correlation.PairCount} separate source-backed DID/CAN pair(s) " +
        $"in the same recording. Match scope: {FieldScope}. " +
        (SnapshotRecordFullyDecoded ? string.Empty :
            "This snapshot record is only partially decoded; unresolved bytes remain. ") +
        "Snapshot-to-signal identity not proven.";
}

/// <summary>
/// Links the DID identifiers actually present in one selected DTC snapshot
/// event to independent UDS↔CAN hypotheses, without mixing physical loggers,
/// guessing source frames or treating a shared DID number as field identity.
/// </summary>
public static class DtcSnapshotCanLinker
{
    public static IReadOnlyList<DtcSnapshotCanLink> Find(
        int originalSourceIndex,
        IReadOnlyList<DecodedDtcRecord> eventRecords,
        IReadOnlyList<UdsCanCorrelationResult> correlations,
        Func<int, int>? sourceAt = null,
        string? observedEcu = null)
    {
        ArgumentNullException.ThrowIfNull(eventRecords);
        ArgumentNullException.ThrowIfNull(correlations);

        // If a physical source has not been independently established, a
        // plausible same-name/same-DID match is not admissible as a link.
        if (originalSourceIndex < 0) return [];
        // A known, source-verified ECU identity must not borrow another ECU's
        // time-series mapping just because DID numbers and logger match.
        if (observedEcu is not null && string.IsNullOrWhiteSpace(observedEcu))
            return [];

        // A source-backed raw byte range is not a measured, physically decoded
        // DID sample. Require one finite decoded value before proposing a CAN
        // time-series candidate; keep raw-only fields in the snapshot inspector.
        var snapshotFields = eventRecords
            .Where(record => record.Kind == DtcRecordKind.Snapshot)
            .SelectMany(record => record.Values.Select(field => (Record: record, Field: field)))
            .Where(item => item.Field.Did is >= 0 && item.Field.HasVerifiedSource &&
                item.Field.NumericValue is { } value && double.IsFinite(value) &&
                (sourceAt is null || AllInSource(item.Field.SourceSpans!
                    .Select(span => span.FrameIndex), originalSourceIndex, sourceAt)))
            .ToArray();
        if (snapshotFields.Length == 0) return [];

        // Keep only correlation results with actual paired original frames
        // and the same physical file. Metadata-only/rejected candidates are
        // available in the main UDS↔DBC workspace, not as snapshot links.
        var candidates = correlations
            .Where(result =>
                result.HasSourcePairs &&
                result.OriginalSourceIndex == originalSourceIndex &&
                (observedEcu is null || string.Equals(result.Candidate.Did.Ecu,
                    observedEcu, StringComparison.OrdinalIgnoreCase)) &&
                result.Verdict is not (CorrelationVerdict.Rejected or CorrelationVerdict.InsufficientData) &&
                result.Points.Count > 0 &&
                (sourceAt is null || result.Points.All(point =>
                    AllInSource(new[] { point.DidFrameIndex, point.SignalFrameIndex }
                        .Concat(point.DidResponseFrameIndices ?? []),
                        originalSourceIndex, sourceAt))))
            .ToLookup(result => result.Candidate.Did.Did);

        var links = new List<DtcSnapshotCanLink>();
        foreach (var item in snapshotFields)
        {
            var field = item.Field;
            foreach (var correlation in candidates[field.Did!.Value])
            {
                // Multi-field DIDs can contain unrelated measurements.
                // A correlation scoped to DID/FieldA must not be presented
                // as a lead from a DTC snapshot's DID/FieldB. A DID-level
                // candidate remains marked explicitly as DID-only.
                var observed = correlation.ObservedField;
                // An undifferentiated DID-level hypothesis must not be reused
                // for several unrelated fields within one freeze-frame DID.
                if (!observed.Contains('/') &&
                    snapshotFields.Count(other =>
                        ReferenceEquals(other.Record, item.Record) &&
                        other.Field.Did == field.Did) > 1)
                    continue;
                if (observed.Contains('/'))
                {
                    if (!field.Name.Contains('/'))
                        continue;
                    var observedField = observed[(observed.LastIndexOf('/') + 1)..].Trim();
                    var snapshotField = field.Name[(field.Name.LastIndexOf('/') + 1)..].Trim();
                    // A matching DID name cannot rescue a mismatched declared
                    // subfield: e.g. DID Speed/Pressure is NOT Speed/Speed.
                    if (!string.Equals(snapshotField, observedField,
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Two nested parameters may share the same leaf name.
                    // Without the complete engineering path, the leaf name
                    // cannot uniquely identify the snapshot's decoded field.
                    if (snapshotFields.Count(other =>
                        ReferenceEquals(other.Record, item.Record) &&
                        other.Field.Did == field.Did && other.Field.Name.Contains('/') &&
                        string.Equals(other.Field.Name[(other.Field.Name.LastIndexOf('/') + 1)..].Trim(),
                            snapshotField, StringComparison.OrdinalIgnoreCase)) > 1)
                        continue;
                }
                var snapshotUnit = DidSignalCorrelator.NormalizeUnit(field.Unit);
                var dbcUnit = DidSignalCorrelator.NormalizeUnit(correlation.Candidate.Signal.Unit);
                if (snapshotUnit.Length > 0 && dbcUnit.Length > 0 &&
                    !string.Equals(snapshotUnit, dbcUnit, StringComparison.Ordinal))
                    continue;
                links.Add(new DtcSnapshotCanLink(field, correlation,
                    item.Record.IsFullyDecoded));
            }
        }

        return links
            .OrderByDescending(link => link.Correlation.PairCount)
            .ThenBy(link => link.Did)
            .ThenBy(link => link.Correlation.Candidate.Database, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool AllInSource(IEnumerable<int> indices, int expected,
        Func<int, int> sourceAt)
    {
        foreach (var index in indices)
        {
            if (index < 0) return false;
            try
            {
                if (sourceAt(index) != expected) return false;
            }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (IndexOutOfRangeException) { return false; }
        }
        return true;
    }
}
