using RbgCanInsight.Analysis;

namespace RbgCanInsight.App.ViewModels;

/// <summary>UI label for two genuine CAN observations. Proximity is not causality.</summary>
public sealed record NearbySignalCandidateRow(PlotSeriesViewModel Series, NearbySignalChange Evidence)
{
    public string Signal => Series.MessageName + "." + Series.SignalName;
    public string CatalogMatchHint => Evidence.CatalogNameHint;
    public string SampleGap => ((Evidence.After.TimestampNanoseconds -
        Evidence.Before.TimestampNanoseconds) / 1e9).ToString("F6") + " s (actual sample interval)";
    public string Source => Series.RecordingText + " · " + Series.ChannelText +
        " · 0x" + Series.IdentifierHex;
    public string TransitionTime => (Evidence.After.TimestampNanoseconds / 1e9).ToString("F6") + " s";
    public string RelativeTime => (Evidence.OffsetNanoseconds / 1e9).ToString("+0.000000;-0.000000;0.000000") + " s";
    public string Values => $"{Evidence.Before.Value:G9} → {Evidence.After.Value:G9}" +
        (string.IsNullOrWhiteSpace(Series.Unit) ? "" : " " + Series.Unit);
    /// <summary>Two genuinely recorded values: an observation, not a proven cause.</summary>
    public string EvidenceKindText => RbgCanInsight.Domain.EvidenceKinds.Label(EvidenceClassification.For(Evidence));
    public string OriginalFrames =>
        $"#{Evidence.Before.FrameIndex + 1} → #{Evidence.After.FrameIndex + 1}";
}
