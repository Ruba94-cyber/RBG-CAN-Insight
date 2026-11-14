namespace RbgCanInsight.Correlation;

/// <summary>Separate raw metadata/temporal fit scores from the conservative
/// association indicator displayed before independent analyst review.</summary>
public static class CorrelationEvidencePolicy
{
    public const double MaximumUnreviewedAssociation = 0.50;

    /// <summary>Presentation indicator only; never a calibrated probability.
    /// An automatic match cannot establish signal identity regardless of fit.</summary>
    public static double AssociationIndicator(double rawMatchScore, bool analystConfirmed)
    {
        if (!double.IsFinite(rawMatchScore)) return 0;
        var bounded = Math.Clamp(rawMatchScore, 0d, 1d);
        return analystConfirmed ? bounded : Math.Min(MaximumUnreviewedAssociation, bounded);
    }
}
