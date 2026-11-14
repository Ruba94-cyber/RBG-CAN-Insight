using RbgCanInsight.Domain;

namespace RbgCanInsight.Correlation;

/// <summary>Evidence type of a DID-to-CAN correlation result.</summary>
public static class CorrelationEvidenceKinds
{
    /// <summary>
    /// Confirmed only when the result carries the reviewed/proven verdict (never emitted by automatic fitting);
    /// candidates are hypotheses; a rejection or insufficient data is a calculated outcome.
    /// </summary>
    public static EvidenceKind For(UdsCanCorrelationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Verdict switch
        {
            CorrelationVerdict.Confirmed => EvidenceKind.Confirmed,
            CorrelationVerdict.Candidate or CorrelationVerdict.StrongCandidate => EvidenceKind.Hypothesis,
            _ => EvidenceKind.Calculated
        };
    }
}
