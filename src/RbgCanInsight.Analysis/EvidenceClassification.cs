using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Evidence type of the Analysis results. Nothing here can return <see cref="EvidenceKind.Confirmed"/>: a signal association
/// found by statistics or timing is at best a hypothesis, whatever its strength.
/// </summary>
public static class EvidenceClassification
{
    /// <summary>A signal-to-signal association is a candidate relationship (Hypothesis); its numbers are Calculated
    /// and the frames behind it Observed.</summary>
    public static EvidenceKind For(SignalRelationship relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        return EvidenceKind.Hypothesis;
    }

    public static EvidenceKind For(SignalMapEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return For(edge.Evidence);
    }

    /// <summary>Two genuinely recorded values around a change: an observation.</summary>
    public static EvidenceKind For(NearbySignalChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return EvidenceKind.Observed;
    }

    /// <summary>Original recorded value changes of two signals: observations (their pairing is not proof of influence).</summary>
    public static EvidenceKind For(SignalChangeWitness witness)
    {
        ArgumentNullException.ThrowIfNull(witness);
        return EvidenceKind.Observed;
    }

    public static string Label(SignalRelationship relationship) => EvidenceKinds.Label(For(relationship));
}
