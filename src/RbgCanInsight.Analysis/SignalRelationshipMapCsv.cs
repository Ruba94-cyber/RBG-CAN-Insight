using System.Globalization;
using System.Text;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Source-evidence-only CSV for engineering reviews. Exports individually
/// observed displayed edges, NEVER inferred/transitive connections or a
/// causal statement. Text fields from untrusted DBC labels are hardened
/// against Excel spreadsheet formula injection; all numeric data remains
/// culture-invariant and exact original frame positions are preserved.
/// </summary>
public static class SignalRelationshipMapCsv
{
    public static string Export(SignalRelationshipMapReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", new[]
        {
            "logger_index_1based", "group_number", "signal_a",
            "signal_b", "dbc_a", "dbc_b", "source_file_a",
            "source_file_b", "can_bus_a_1based",
            "can_bus_b_1based", "wire_id_a_hex", "wire_id_b_hex",
            "same_recorded_can_message", "evidence_kind",
            "level_pearson", "change_pearson", "pair_count",
            "reference_changes", "candidate_changes",
            "estimated_candidate_lead_ms", "source_frame_a_1based",
            "source_frame_b_1based", "timestamp_a_ns",
            "timestamp_b_ns", "engineering_check", "interpretation_limit",
            "signals_considered", "qualified_signals", "total_detected_links",
            "displayed_links", "selection_limited", "display_limited",
            "reference_samples_in_window", "reference_samples_analyzed", "reference_subsampled",
            "evidence_class"
        }));
        // A header-only CSV when no relationships exist is intentional.
        // Never imply that the absence of edges proves independence.
        foreach (var group in report.Groups)
        {
            foreach (var edge in group.Edges)
            {
                var e = edge.Evidence;
                builder.AppendLine(string.Join(",", new[]
                {
                    N(report.OriginalSourceIndex + 1),
                    N(group.Number),
                    Text(edge.A.Message.Name + "." + edge.A.Signal.Name),
                    Text(edge.B.Message.Name + "." + edge.B.Signal.Name),
                    Text(edge.A.DatabaseName),
                    Text(edge.B.DatabaseName),
                    Text(edge.A.OriginalSourcePath),
                    Text(edge.B.OriginalSourcePath),
                    N(edge.A.Channel + 1),
                    N(edge.B.Channel + 1),
                    Text($"0x{edge.A.SourceIdentifier.ToString(edge.A.SourceIsExtended ? "X8" : "X3", CultureInfo.InvariantCulture)}"),
                    Text($"0x{edge.B.SourceIdentifier.ToString(edge.B.SourceIsExtended ? "X8" : "X3", CultureInfo.InvariantCulture)}"),
                    edge.SameRecordedMessage ? "true" : "false",
                    Text(e.AssociationType),
                    e.IsChangeSequenceOnly ? "" : R(e.LevelPearson),
                    e.ChangePearson is { } changes && !e.IsChangeSequenceOnly ? R(changes) : "",
                    N(e.PairedSamples),
                    N(e.ReferenceChanges),
                    N(e.CandidateChanges),
                    R(e.CandidateLeadNanoseconds / 1_000_000d),
                    N(e.ReferenceFrameIndex + 1),
                    N(e.CandidateFrameIndex + 1),
                    N(e.ReferenceTimestampNanoseconds),
                    N(e.CandidateTimestampNanoseconds),
                    Text(edge.InvestigationHint),
                    Text("Observed correlation / timing only; not a verified ECU dependency or cause."),
                    N(report.ExaminedSignals),
                    N(report.EligibleSignals),
                    N(report.TotalEdgesDetected),
                    N(report.Edges.Count),
                    report.SignalSelectionTruncated ? "true" : "false",
                    report.EdgeSelectionTruncated ? "true" : "false",
                    // Level-correlation sampling of the reference signal (blank for change-sequence hints, which scan the whole window).
                    e.IsChangeSequenceOnly ? "" : N(e.ReferenceSamplesInWindow),
                    e.IsChangeSequenceOnly ? "" : N(e.ReferenceSamplesAnalyzed),
                    e.IsChangeSequenceOnly ? "" : (e.ReferenceWasSubsampled ? "true" : "false"),
                    Text(EvidenceKinds.Label(EvidenceClassification.For(edge)))
                }));
            }
        }
        return builder.ToString();
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Text(string? value)
    {
        var input = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        // A DBC label cannot smuggle a second CSV record or a hidden
        // spreadsheet formula through embedded line breaks or tabs.
        // Formula injection protection is needed because DBC labels and
        // comments come from the trace user's filesystem.
        var trimmed = input.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))
            input = "'" + input;
        return "\"" + input.Replace("\"", "\"\"") + "\"";
    }
}
