using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.Correlation;

/// <summary>Read-only review decision for a saved DID/CAN association. The
/// semantic fingerprints are change detectors, not certificates of authenticity.
/// A legacy mapping without fingerprints remains loadable but unverified.</summary>
public sealed record MappingReviewAssessment(bool MayRetainReview, string Reason);

public static class MappingReviewGuard
{
    public static MappingReviewAssessment Assess(
        DidSignalMapping saved, DidParameter did,
        string databaseName, DbcMessage message, DbcSignal signal)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(did);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signal);
        if (saved.Did != did.Did || !string.Equals(saved.Ecu, did.Ecu, StringComparison.OrdinalIgnoreCase))
            return Changed("ECU or DID identity has changed");
        if (!string.Equals(saved.DidName, did.Name, StringComparison.OrdinalIgnoreCase))
            return Changed("DID name differs from the saved mapping");
        if (!string.Equals(saved.Database, databaseName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(saved.Message, message.Name, StringComparison.Ordinal) ||
            !string.Equals(saved.Signal, signal.Name, StringComparison.Ordinal) ||
            saved.MessageIdentifier != message.Identifier || saved.IsExtended != message.IsExtended)
            return Changed("CAN database, message or arbitration identity changed");
        if (string.IsNullOrWhiteSpace(saved.DidDefinitionFingerprint) ||
            string.IsNullOrWhiteSpace(saved.CanSignalFingerprint))
            return Changed("Legacy map has no definition fingerprints; review this association again");
        if (!string.Equals(saved.DidDefinitionFingerprint,
                MappingDefinitionFingerprint.ForDid(did), StringComparison.Ordinal))
            return Changed("CDD/ODX DID definition changed; review source fields and variants");
        if (!string.Equals(saved.CanSignalFingerprint,
                MappingDefinitionFingerprint.ForSignal(databaseName, message, signal),
                StringComparison.Ordinal))
            return Changed("DBC signal definition changed; review bits, scaling, mux and values");
        return new MappingReviewAssessment(true, string.Empty);
    }

    private static MappingReviewAssessment Changed(string reason) => new(false, reason);
}
