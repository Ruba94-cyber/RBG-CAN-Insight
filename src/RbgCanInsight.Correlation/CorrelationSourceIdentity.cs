using RbgCanInsight.Dbc;
using RbgCanInsight.Analysis;

namespace RbgCanInsight.Correlation;

/// <summary>
/// Exact identity guard for navigation between observed UDS/CAN correlations
/// and DBC declarations. Name-only matching is unsafe when databases and
/// physical buses reuse the same arbitration ID or signal names.
/// </summary>
public static class CorrelationSourceIdentity
{
    public static bool MatchesDefinition(
        UdsCanCorrelationResult result,
        string database,
        uint messageIdentifier,
        bool isExtended,
        string messageName,
        string signalName) =>
        string.Equals(result.Candidate.Database, database, StringComparison.OrdinalIgnoreCase) &&
        result.Candidate.Message.Identifier == messageIdentifier &&
        result.Candidate.Message.IsExtended == isExtended &&
        string.Equals(result.Candidate.Message.Name, messageName, StringComparison.Ordinal) &&
        string.Equals(result.Candidate.Signal.Name, signalName, StringComparison.Ordinal);

    /// <summary>
    /// A selected CAN channel may only match an actual observed, paired
    /// source. Missing/insufficient evidence must not be treated as a match.
    /// A null channel means the caller is viewing all physical channels,
    /// not that any unobserved channel has been proven.
    /// </summary>
    /// <summary>Match the EXACT observed DBC timeline behind a source-paired
    /// DID/CAN hypothesis. All declared and physical identities must match,
    /// including the logger and actual J1939 wire ID.</summary>
    public static bool MatchesObservedTimeline(
        UdsCanCorrelationResult result, SignalTimeline timeline) =>
        result.HasSourcePairs &&
        result.OriginalSourceIndex >= 0 &&
        result.SignalSourceIdentifier is { } wireId &&
        result.SignalSourceIsExtended is { } wireExtended &&
        timeline.OriginalSourceIndex == result.OriginalSourceIndex &&
        timeline.Channel == result.SignalChannel &&
        timeline.SourceIdentifier == wireId &&
        timeline.SourceIsExtended == wireExtended &&
        MatchesDefinition(result, timeline.DatabaseName,
            timeline.Message.Identifier, timeline.Message.IsExtended,
            timeline.Message.Name, timeline.Signal.Name);

    public static bool MatchesObservedChannel(UdsCanCorrelationResult result, int? channel) =>
        result.HasSourcePairs &&
        (channel is null || result.SignalChannel == channel.Value);
}
