namespace RbgCanInsight.Domain;

/// <summary>
/// What kind of statement a result row makes. A statistical association never proves causality, so the kinds are kept apart
/// in every report, CSV and window instead of being implied by wording.
/// </summary>
public enum EvidenceKind
{
    /// <summary>Read directly from the trace: recorded frames, raw values, timestamps.</summary>
    Observed,
    /// <summary>Derived from observed data by an algorithm: coefficients, lags, counts, fitted or predicted values.</summary>
    Calculated,
    /// <summary>A candidate relationship or possible cause suggested by the data; not proven.</summary>
    Hypothesis,
    /// <summary>Directly supported by an explicit reference (documented CDD/ODX/DBC definition or an analyst-reviewed mapping).</summary>
    Confirmed
}

public static class EvidenceKinds
{
    public static string Label(EvidenceKind kind) => kind switch
    {
        EvidenceKind.Observed => "Observed",
        EvidenceKind.Calculated => "Calculated",
        EvidenceKind.Hypothesis => "Hypothesis",
        EvidenceKind.Confirmed => "Confirmed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Description(EvidenceKind kind) => kind switch
    {
        EvidenceKind.Observed => "read directly from the recorded trace",
        EvidenceKind.Calculated => "derived from observed data (coefficients, lags, counts)",
        EvidenceKind.Hypothesis => "candidate relationship suggested by the data, not proven",
        EvidenceKind.Confirmed => "backed by an explicit reference or analyst review",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>One-line legend for windows that show relationship or correlation results.</summary>
    public static string Legend =>
        "Evidence types: " + string.Join(" · ", Enum.GetValues<EvidenceKind>()
            .Select(kind => $"{Label(kind)} = {Description(kind)}")) + ".";
}
