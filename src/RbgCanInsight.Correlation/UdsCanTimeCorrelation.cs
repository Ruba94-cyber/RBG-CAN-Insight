using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.Correlation;

public enum CorrelationVerdict
{
    /// <summary>Reserved for separately reviewed or independently proven signal identity.
    /// Automatic time-series fitting must never emit this verdict.</summary>
    Confirmed,
    Candidate,
    Rejected,
    InsufficientData,
    /// <summary>Strong statistical agreement only: not proof of DID-to-CAN identity.</summary>
    StrongCandidate
}

public sealed record CorrelationPoint(
    long DidTimestampNanoseconds,
    long SignalTimestampNanoseconds,
    double DidValue,
    double SignalValue,
    double PredictedSignalValue,
    double Error,
    int DidFrameIndex,
    int SignalFrameIndex,
    string DidField = "",
    IReadOnlyList<int>? DidResponseFrameIndices = null);

public sealed record UdsCanCorrelationResult(
    CorrelationCandidate Candidate,
    CorrelationVerdict Verdict,
    double Confidence,
    double MetadataConfidence,
    double ValueConfidence,
    int PairCount,
    double Coverage,
    double LagMilliseconds,
    double Pearson,
    double RSquared,
    double Slope,
    double Intercept,
    double MeanAbsoluteError,
    double RootMeanSquaredError,
    string Explanation,
    IReadOnlyList<CorrelationPoint> Points,
    bool AmbiguousLag = false,
    bool AmbiguousSignal = false)
{
    /// <summary>Physical zero-based CAN channel that supplied the fitted
    /// recorded signal timeline. -1 means correlation did not establish
    /// one unique observed CAN source. Never infer it from a DBC name.</summary>
    public int SignalChannel { get; init; } = -1;
    /// <summary>Original physical logger of both DID response and CAN signal,
    /// if independently verified. -1 means not established.</summary>
    public int OriginalSourceIndex { get; init; } = -1;
    /// <summary>The actual observed CAN wire ID for the paired signal
    /// source (J1939 PGN aliases can have a different ECU source address
    /// from the matching DBC message declaration). Null when unproven.</summary>
    public uint? SignalSourceIdentifier { get; init; }
    public bool? SignalSourceIsExtended { get; init; }
    public bool HasSourcePairs => SignalChannel >= 0 && PairCount > 0;
    public string SignalSourceText => !HasSourcePairs
        ? $"{Candidate.Database} · observed paired CAN source not established"
        : $"{Candidate.Database} · " +
          (OriginalSourceIndex >= 0 ? $"Logger {OriginalSourceIndex + 1} · " : "") +
          $"CAN {SignalChannel + 1} · " +
          (SignalSourceIdentifier is { } recorded
              ? $"recorded 0x{recorded:X}{(SignalSourceIsExtended == true ? " extended" : " standard")}"
              : "original recorded CAN identifier not established");

    public string DidText => Candidate.Did.DidHex;
    public string DidName => Candidate.Did.DisplayName;
    public string SignalText => $"{Candidate.Message.Name}.{Candidate.Signal.Name}";
    /// <summary>Raw metadata/temporal agreement score; useful for ranking but NOT
    /// a probability of signal identity. Preserved separately in reports.</summary>
    public double StatisticalFitScore => Confidence;
    public string StatisticalFitScoreText => $"{StatisticalFitScore:P0}";
    /// <summary>A conservative presentation indicator: automated fitting cannot
    /// independently establish physical signal identity, even with perfect R².</summary>
    public double AssociationConfidence => CorrelationEvidencePolicy.AssociationIndicator(
        Confidence, Verdict == CorrelationVerdict.Confirmed);
    public string ConfidenceText => $"{AssociationConfidence:P0}";
    public string MetricsText => PairCount == 0 ? "No paired values" :
        $"{PairCount} pairs · coverage {Coverage:P0} · lag {LagMilliseconds:+0.##;-0.##;0} ms · R² {RSquared:F3} · MAE {MeanAbsoluteError:G5}";

    public string EvidenceSummary => PairCount == 0 ? Explanation :
        (AmbiguousSignal ? "Multiple plausible CAN signals — confirmation withheld · " :
            AmbiguousLag ? "Multiple plausible lags — confirmation withheld · " :
            string.Empty) + MetricsText +
        $" · statistical fit {StatisticalFitScoreText} · unreviewed CAN identity";

    public string ObservedField => Points.Count == 0
        ? "Not established"
        : Points[0].DidField;

    /// <summary>Zero-based indices in the original imported frame store.
    /// These are source references, not generated correlation row numbers.</summary>
    public string SourceFrameEvidence => Points.Count == 0
        ? "No matching source-frame evidence."
        : $"DID source frame indices: {IndexPreview(Points.Select(point => point.DidFrameIndex))}" +
          Environment.NewLine +
          $"CAN source frame indices: {IndexPreview(Points.Select(point => point.SignalFrameIndex))}" +
          (Points.Any(point => point.DidResponseFrameIndices is { Count: > 0 })
              ? Environment.NewLine +
                $"DID response transport frames (all ISO-TP segments): " +
                IndexPreview(Points.SelectMany(point =>
                    point.DidResponseFrameIndices ?? []))
              : string.Empty);

    private static string IndexPreview(IEnumerable<int> indices)
    {
        var selected = indices.Where(index => index >= 0).Distinct().Take(13).ToArray();
        if (selected.Length == 0) return "unknown";
        return string.Join(", ", selected.Take(12)) + (selected.Length > 12 ? ", …" : "");
    }
}

public sealed record TimeCorrelationOptions(
    int MaxCandidates = 8,
    double MaximumLagMilliseconds = 1000,
    double LagStepMilliseconds = 25,
    double MaximumPairDistanceMilliseconds = 250,
    int MinimumPairsForCandidate = 3,
    int MinimumPairsForConfirmation = 5);

/// <summary>Correlates physical DID samples with decoded DBC signal timelines. Candidate names
/// and metadata only define a shortlist; the final verdict is driven by timestamp-aligned values,
/// fitted scale/offset, lag, coverage and residual error.</summary>
public static class UdsCanTimeCorrelation
{
    public static IReadOnlyList<UdsCanCorrelationResult> Correlate(
        IReadOnlyList<DiagnosticDidSample> didSamples,
        IReadOnlyList<CorrelationCandidate> candidates,
        IReadOnlyList<SignalTimeline> timelines,
        TimeCorrelationOptions? options = null,
        Func<int, int>? sourceAt = null)
    {
        options ??= new TimeCorrelationOptions();
        var invalidOptions = ValidateOptions(options);
        if (invalidOptions.Length > 0)
            // An invalid negative candidate limit must not quietly return
            // an empty UI table. Surface at least one actionable failure if
            // a candidate exists; a valid zero limit still selects nothing.
            return candidates.Take(options.MaxCandidates > 0 ? options.MaxCandidates : 1)
                .Select(candidate => Insufficient(candidate, invalidOptions)).ToArray();
        // Nonfinite UDS values cannot support numeric correlation. Their
        // source frames remain available in the diagnostic transaction view.
        var numeric = didSamples.Where(sample =>
                sample.NumericValue is { } value && double.IsFinite(value))
            .OrderBy(sample => sample.TimestampNanoseconds).ToArray();
        var result = new List<UdsCanCorrelationResult>();
        foreach (var candidate in candidates.Take(options.MaxCandidates))
        {
            // Do not treat the same DID number seen on different ECU diagnostic buses
            // as one physical signal. The diagnostic channel comes from the ISO-TP reply.
            var matchingDid = numeric.Where(sample => sample.Did == candidate.Did.Did).ToArray();
            if (matchingDid.Length == 0)
            {
                result.Add(Insufficient(candidate, "No matching numeric DID samples are available."));
                continue;
            }
            var physicalSource = -1;
            if (sourceAt is not null)
            {
                // The DID value's terminal response and every ISO-TP response
                // segment must belong to one physical recording. A common
                // timestamp/channel cannot establish shared logger identity.
                var sources = matchingDid.Select(sample => sample.FrameIndex < 0
                    ? -1 : sourceAt(sample.FrameIndex)).Distinct().ToArray();
                if (sources.Length != 1 || sources[0] < 0)
                {
                    result.Add(Insufficient(candidate,
                        "DID responses span multiple original recordings or have unknown logger evidence. Select one logger."));
                    continue;
                }
                physicalSource = sources[0];
                if (matchingDid.Any(sample => sample.ResponseFrames.Any(frameIndex =>
                        frameIndex < 0 || sourceAt(frameIndex) != physicalSource)))
                {
                    result.Add(Insufficient(candidate,
                        "The DID ISO-TP response includes a frame from another recording; cross-logger correlation is not permitted."));
                    continue;
                }
            }
            // Reject explicitly untrusted diagnostic transport/endpoint
            // labels before ranking temporal evidence. A perfectly matching
            // numeric waveform cannot repair a malformed reassembly or
            // establish the physical ECU behind an unknown reply address.
            if (matchingDid.Any(sample =>
                    sample.Quality.StartsWith("Malformed", StringComparison.OrdinalIgnoreCase) ||
                    sample.Quality.StartsWith("Unknown endpoints", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(Insufficient(candidate,
                    "Recorded DID source has malformed transport or unknown diagnostic endpoint; verify the ISO-TP reply before correlation."));
                continue;
            }
            // One DID can expose multiple, unrelated numeric response fields.
            // Never fit a synthetic series interleaving their values. The UI
            // groups by field before invoking this engine; callers supplying
            // a mixed set must receive an explicit insufficient-data result.
            var didFields = matchingDid.Select(sample => sample.Field)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (didFields.Length > 1)
            {
                result.Add(Insufficient(candidate,
                    "DID observations contain multiple fields; analyze each source field separately."));
                continue;
            }
            var didEcus = matchingDid.Select(sample => sample.Ecu)
                .Where(ecu => !string.IsNullOrWhiteSpace(ecu))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (didEcus.Length > 1)
            {
                result.Add(Insufficient(candidate, "DID samples belong to multiple ECUs; separate diagnostic endpoints before correlation."));
                continue;
            }
            if (matchingDid.Any(sample => string.IsNullOrWhiteSpace(sample.Ecu)))
            {
                result.Add(Insufficient(candidate,
                    "DID ECU identity is unknown for some observations; do not combine them with a named endpoint."));
                continue;
            }
            if (didEcus.Length == 1 &&
                !string.IsNullOrWhiteSpace(candidate.Did.Ecu) &&
                !string.Equals(candidate.Did.Ecu, didEcus[0],
                    StringComparison.OrdinalIgnoreCase))
            {
                result.Add(Insufficient(candidate,
                    "Observed DID ECU does not match the source catalog ECU; review endpoint identity."));
                continue;
            }

            // A temporal regression can fit almost any two monotonic
            // quantities. Matching timestamps and slopes cannot override
            // incompatible source-declared physical units.
            var observedUnits = matchingDid
                .Select(sample => DidSignalCorrelator.NormalizeUnit(sample.Unit))
                .Where(unit => unit.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (observedUnits.Length > 1)
            {
                result.Add(Insufficient(candidate,
                    "DID observations use incompatible physical units; separate source fields or definitions."));
                continue;
            }
            var catalogUnit = DidSignalCorrelator.NormalizeUnit(candidate.Did.Unit);
            var observedUnit = observedUnits.Length == 1 ? observedUnits[0] : "";
            var signalUnit = DidSignalCorrelator.NormalizeUnit(candidate.Signal.Unit);
            if (observedUnit.Length > 0 && catalogUnit.Length > 0 &&
                observedUnit != catalogUnit ||
                signalUnit.Length > 0 && observedUnit.Length > 0 &&
                signalUnit != observedUnit ||
                signalUnit.Length > 0 && catalogUnit.Length > 0 &&
                signalUnit != catalogUnit)
            {
                result.Add(Insufficient(candidate,
                    "DID and CAN source definitions have incompatible physical units; a fitted numeric trend is not proof of mapping."));
                continue;
            }

            // A double-based CAN series is not lossless for raw integer
            // definitions wider than 53 bits. Exact recorded raw bytes are
            // available in the DBC inspector, but not in these timelines.
            if (candidate.Signal.ValueType == DbcSignalValueType.Integer &&
                candidate.Signal.BitLength > 53)
            {
                result.Add(Insufficient(candidate,
                    "Wide CAN integer (>53 bits): double-based signal timeline lacks exact raw evidence. Review the recorded DBC bit inspector."));
                continue;
            }

            var didChannels = matchingDid.Where(sample => sample.Channel >= 0)
                .Select(sample => sample.Channel).Distinct().ToArray();
            if (didChannels.Length == 1 &&
                matchingDid.Any(sample => sample.Channel < 0))
            {
                result.Add(Insufficient(candidate,
                    "DID observations mix known and unknown physical CAN channels; split the endpoint evidence."));
                continue;
            }
            if (didChannels.Length > 1)
            {
                result.Add(Insufficient(candidate, "DID samples span multiple physical CAN channels; split observations by ECU and channel."));
                continue;
            }
            // Database, message and signal alone are insufficient when one DBC is
            // decoded on two physically independent buses.
            var possible = timelines.Where(item =>
                string.Equals(item.DatabaseName, candidate.Database, StringComparison.OrdinalIgnoreCase) &&
                item.Message == candidate.Message && item.Signal == candidate.Signal &&
                (didChannels.Length == 0 || item.Channel == didChannels[0]) &&
                (sourceAt is null || item.OriginalSourceIndex == physicalSource))
                .ToArray();
            if (possible.Length != 1)
            {
                result.Add(Insufficient(candidate,
                    possible.Length == 0 ? "No DBC timeline exists on the diagnostic CAN channel."
                        : "Ambiguous physical CAN sources (channel or J1939 source address): no unique recorded signal timeline can be correlated."));
                continue;
            }
            var timeline = possible[0];
            // A uniquely matching CAN signal from a known physical logger
            // is not enough: without DID original-frame provenance, a legacy
            // caller could mistake independent acquisitions for one ECU.
            if (sourceAt is null && timeline.OriginalSourceIndex >= 0)
            {
                result.Add(Insufficient(candidate,
                    "Original logger mapping was not provided for DID evidence; the CAN signal belongs to a scoped recording."));
                continue;
            }
            if (timeline.Samples.Count == 0)
            {
                result.Add(Insufficient(candidate, "The signal timeline contains no samples."));
                continue;
            }

            result.Add(Evaluate(candidate, matchingDid, timeline, options) with
            {
                OriginalSourceIndex = physicalSource
            });
        }

        return FlagIndistinguishableCandidates(result)
            .OrderByDescending(item => item.Confidence).ToArray();
    }

    /// <summary>Time evidence may validate an observed trend while failing to
    /// identify which of two different DBC signals carries it. Treat this as
    /// a ranking ambiguity, never as two confirmed DID-to-signal mappings.
    /// Compare only the same DID, source field and ECU; candidates from
    /// different source fields are different correlation investigations.</summary>
    private static IReadOnlyList<UdsCanCorrelationResult> FlagIndistinguishableCandidates(
        IReadOnlyList<UdsCanCorrelationResult> results)
    {
        if (results.Count < 2) return results;
        var corrected = new UdsCanCorrelationResult[results.Count];
        for (var index = 0; index < results.Count; index++)
        {
            var current = results[index];
            if (current.Verdict != CorrelationVerdict.StrongCandidate)
            {
                corrected[index] = current;
                continue;
            }

            var competing = results.Where(other =>
                !ReferenceEquals(other, current) &&
                other.PairCount >= 5 &&
                other.Verdict is CorrelationVerdict.StrongCandidate or CorrelationVerdict.Candidate &&
                other.Candidate.Did.Did == current.Candidate.Did.Did &&
                string.Equals(other.Candidate.Did.Ecu, current.Candidate.Did.Ecu,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(other.ObservedField, current.ObservedField,
                    StringComparison.OrdinalIgnoreCase) &&
                // Alternative database aliases for the exact same signal
                // object are not independent competing physical signals.
                (!ReferenceEquals(other.Candidate.Message, current.Candidate.Message) ||
                 !ReferenceEquals(other.Candidate.Signal, current.Candidate.Signal)) &&
                other.ValueConfidence >= current.ValueConfidence - 0.025 &&
                other.Confidence >= current.Confidence - 0.025 &&
                Math.Abs(other.Coverage - current.Coverage) <= 0.05)
                .Take(4).ToArray();

            corrected[index] = competing.Length > 0
                ? current with
                {
                    Verdict = CorrelationVerdict.Candidate,
                    Confidence = Math.Min(current.Confidence, 0.81),
                    AmbiguousSignal = true,
                    Explanation = current.Explanation +
                        "; multiple distinct CAN signals explain this DID almost equally well: " +
                        string.Join(", ", competing.Take(3).Select(other =>
                            other.Candidate.Message.Name + "." + other.Candidate.Signal.Name)) +
                        (competing.Length > 3 ? ", …" : "") +
                        "; manual signal identity verification required"
                }
                : current;
        }
        return corrected;
    }

    private static string ValidateOptions(TimeCorrelationOptions options)
    {
        // Caller thresholds of 1 or 2 are accepted for backwards
        // compatibility, but Evaluate enforces a hard floor of 3 candidate
        // samples and 5 independent frames for confirmation. Reject only
        // genuinely invalid nonpositive thresholds.
        if (options.MaxCandidates < 0 ||
            options.MinimumPairsForCandidate < 1 ||
            options.MinimumPairsForConfirmation < 1 ||
            !double.IsFinite(options.MaximumLagMilliseconds) ||
            !double.IsFinite(options.LagStepMilliseconds) ||
            !double.IsFinite(options.MaximumPairDistanceMilliseconds) ||
            options.MaximumLagMilliseconds < 0 ||
            options.LagStepMilliseconds <= 0 ||
            options.MaximumPairDistanceMilliseconds < 0)
            return "Invalid time-correlation options: counts, lag and distances must be positive and finite.";

        // A 60-second cap is generous for offline ECU-to-signal correlation;
        // huge timestamps and excessively broad pairing windows are not
        // evidence of one physically related signal.
        if (options.MaximumLagMilliseconds > 60000 ||
            options.MaximumPairDistanceMilliseconds > 60000)
            return "Invalid or unsupported temporal lag / pair-distance settings.";

        // Conversion to nanoseconds and the lag scan must be bounded.
        const double MaximumConvertibleMilliseconds = (long.MaxValue / 1_000_000d) / 2;
        if (options.MaximumLagMilliseconds > MaximumConvertibleMilliseconds ||
            options.LagStepMilliseconds > MaximumConvertibleMilliseconds ||
            options.MaximumPairDistanceMilliseconds > MaximumConvertibleMilliseconds)
            return "Time-correlation lag or tolerance exceeds safe timestamp limits.";

        var maxLag = options.MaximumLagMilliseconds * 1_000_000d;
        // A caller may request sub-millisecond lag resolution. Preserve it
        // down to one nanosecond rather than silently rounding to 1 ms.
        var step = Math.Max(1d, options.LagStepMilliseconds * 1_000_000d);
        // The grid contains zero and both ends of the search range even
        // when maxLag is not evenly divisible by step. Count both sides
        // rather than truncating and unintentionally exceeding 2,001 trials.
        if (Math.Ceiling(maxLag / step) > 1000)
            return "Time-correlation lag search exceeds the 2,001-step safety limit; increase the lag step.";
        return string.Empty;
    }

    private static UdsCanCorrelationResult Evaluate(
        CorrelationCandidate candidate,
        IReadOnlyList<DiagnosticDidSample> didSamples,
        SignalTimeline timeline,
        TimeCorrelationOptions options)
    {
        // Correlate validates the finite values, maximum 60-second window,
        // timestamp-conversion safety and 2,001-step bound before Evaluate.
        var maximumLag = (long)(options.MaximumLagMilliseconds * 1_000_000d);
        var lagStep = Math.Max(1L,
            (long)Math.Ceiling(options.LagStepMilliseconds * 1_000_000d));
        var maxDistance = (long)(options.MaximumPairDistanceMilliseconds * 1_000_000d);
        // Diagnostic replies from distinct source frames may also share a
        // logger timestamp. An identical reading is one temporal observation;
        // conflicting values at the same instant are not a fit candidate.
        // Keep the original sample count as the coverage denominator, so
        // duplicates cannot inflate confidence.
        var didObservations = ConsolidateSimultaneousDidSamples(didSamples);
        if (didObservations.Count == 0)
            return Insufficient(candidate,
                "No unambiguous DID observations remain after resolving duplicate timestamps.");
        // Duplicate CAN timestamps are common in merged logger captures.
        // Collapse identical readings to one time point and quarantine
        // contradictory simultaneous values: none identifies a unique
        // numeric CAN value for the diagnostic timestamp.
        var signalSamples = ConsolidateSimultaneousSamples(timeline.Samples);
        if (!signalSamples.Any(sample => double.IsFinite(sample.Value)))
            return Insufficient(candidate,
                "No unambiguous finite CAN samples remain after resolving simultaneous timestamps.");
        Fit? best = null;
        // Keep just the score/lag of each hypothesis, not another full list
        // of fit points. Periodic CAN signals can fit at several distinct
        // lags; a single high R² is not evidence of a unique alignment.
        var lagHypotheses = new List<(long Lag, double Score)>();
        // Center on lag=0 so a non-divisible step cannot silently skip
        // exact simultaneity. Include both search bounds independently.
        // ValidateOptions caps this symmetric grid at 2,001 hypotheses.
        var lags = new List<long> { 0 };
        for (var offset = lagStep; offset < maximumLag; offset += lagStep)
        {
            lags.Add(-offset);
            lags.Add(offset);
        }
        if (maximumLag > 0)
        {
            lags.Add(-maximumLag);
            lags.Add(maximumLag);
        }
        foreach (var lag in lags)
        {
            var pairs = Pair(didObservations, signalSamples, lag, maxDistance);
            if (pairs.Count < 2) continue;
            var fit = FitValues(pairs, lag, didSamples.Count);
            // Finite source values can still overflow regression variance
            // or residual arithmetic. A NaN score must never rank a mapping.
            if (!double.IsFinite(fit.Score) || !double.IsFinite(fit.Pearson) ||
                !double.IsFinite(fit.RSquared) || !double.IsFinite(fit.Mae) ||
                !double.IsFinite(fit.Slope) || !double.IsFinite(fit.Intercept))
                continue;
            lagHypotheses.Add((lag, fit.Score));
            if (best is null || fit.Score > best.Score) best = fit;
        }

        if (best is null)
        {
            return Insufficient(candidate, "No samples could be paired inside the configured time tolerance.");
        }

        // A distinct lag with nearly the same goodness-of-fit means the
        // temporal evidence does not identify one delay. Adjacent grid points
        // within the lag resolution are one hypothesis, not two independent
        // alternatives. Periodic waveforms may otherwise look fully confirmed
        // at both lag 0 and at an entire signal period.
        var separateLag = lagStep > long.MaxValue / 3
            ? long.MaxValue : Math.Max(5_000_000L, lagStep * 3);
        var ambiguousLag = best.Score > 0 &&
            lagHypotheses.Any(item =>
                Math.Abs(item.Lag - best.LagNanoseconds) >= separateLag &&
                item.Score >= best.Score * 0.975);
        // Constant series cannot establish an observed relationship, even when their
        // fitted error is zero. Require independent variation before confirmation.
        var variableDid = best.Points.Select(point => point.DidValue).Distinct().Take(2).Count() > 1;
        var variableCan = best.Points.Select(point => point.SignalValue).Distinct().Take(2).Count() > 1;
        var independentCanSamples = best.Points.Select(point => point.SignalFrameIndex).Distinct().Count();
        var independentDidSamples = best.Points.Select(point => point.DidFrameIndex).Distinct().Count();
        // A user/imported preset must not reduce scientific evidence below
        // three distinct candidate observations or five for confirmation.
        // Repeated DID responses and held CAN values are not independent.
        var minimumCandidatePairs = Math.Max(3, options.MinimumPairsForCandidate);
        var minimumConfirmedPairs = Math.Max(5,
            Math.Max(minimumCandidatePairs, options.MinimumPairsForConfirmation));
        var enough = best.Points.Count >= minimumCandidatePairs &&
                     independentCanSamples >= minimumCandidatePairs &&
                     independentDidSamples >= minimumCandidatePairs &&
                     variableDid && variableCan;
        var valueConfidence = enough
            ? Math.Clamp((Math.Abs(best.Pearson) * 0.35 + best.RSquared * 0.35 + best.ErrorScore * 0.30) *
                         best.Coverage * best.AlignmentScore, 0, 1)
            : 0;
        var confidence = enough
            ? Math.Clamp(candidate.Confidence * 0.35 + valueConfidence * 0.65, 0, 1)
            : candidate.Confidence * 0.35;
        // Never assign strong-candidate verdict to a waveform with two independently
        // plausible lag alignments, even if one fit's raw score is excellent.
        if (ambiguousLag)
            confidence = Math.Min(confidence, 0.81);
        var verdict = !enough
            ? CorrelationVerdict.InsufficientData
            : confidence >= 0.82 && best.Points.Count >= minimumConfirmedPairs &&
              independentCanSamples >= minimumConfirmedPairs &&
              independentDidSamples >= minimumConfirmedPairs &&
              best.Coverage >= 0.55
                ? CorrelationVerdict.StrongCandidate
                : confidence >= 0.55 ? CorrelationVerdict.Candidate : CorrelationVerdict.Rejected;
        var explanation = string.Join("; ", new[]
        {
            $"DID field '{didSamples[0].Field}' on ECU '{didSamples[0].Ecu}'",
            $"{best.Points.Count}/{didSamples.Count} values aligned ({best.Coverage:P0} coverage)",
            $"{independentCanSamples} distinct CAN source frames",
            $"{independentDidSamples} distinct DID source frames",
            $"lag {best.LagNanoseconds / 1_000_000d:+0.##;-0.##;0} ms",
            $"mean timestamp error {best.MeanAlignmentNanoseconds / 1_000_000d:F2} ms",
            ambiguousLag ? "multiple plausible lags: periodic or repeated evidence prevents unique temporal confirmation" : string.Empty,
            $"Pearson {best.Pearson:F3}",
            $"R² {best.RSquared:F3}",
            $"fit y={best.Slope.ToString("G5", CultureInfo.InvariantCulture)}x{best.Intercept:+0.#####;-0.#####;+0}",
            $"MAE {best.Mae.ToString("G5", CultureInfo.InvariantCulture)}",
            !variableDid || !variableCan ? "insufficient independent value variation" : string.Empty,
            independentCanSamples < minimumCandidatePairs
                ? "insufficient independent CAN samples" : string.Empty,
            independentDidSamples < minimumCandidatePairs
                ? "insufficient independent DID source frames" : string.Empty,
            "statistical association only; DID/CAN identity requires independent source or analyst verification",
            candidate.ReasonText
        }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return new UdsCanCorrelationResult(
            candidate, verdict, confidence, candidate.Confidence, valueConfidence,
            best.Points.Count, best.Coverage, best.LagNanoseconds / 1_000_000d,
            best.Pearson, best.RSquared, best.Slope, best.Intercept, best.Mae, best.Rmse,
            explanation, best.Points, ambiguousLag)
        {
            SignalChannel = timeline.Channel,
            SignalSourceIdentifier = timeline.SourceIdentifier,
            SignalSourceIsExtended = timeline.SourceIsExtended
        };
    }

    private static IReadOnlyList<DiagnosticDidSample> ConsolidateSimultaneousDidSamples(
        IReadOnlyList<DiagnosticDidSample> samples)
    {
        // Correlate sorts DID samples by timestamp before calling Evaluate.
        // The same clock tick cannot supply multiple independent time points.
        var unique = new List<DiagnosticDidSample>(samples.Count);
        for (var i = 0; i < samples.Count;)
        {
            var sample = samples[i];
            var conflicting = false;
            var next = i + 1;
            while (next < samples.Count &&
                   samples[next].TimestampNanoseconds == sample.TimestampNanoseconds)
            {
                if (samples[next].NumericValue != sample.NumericValue)
                    conflicting = true;
                next++;
            }
            if (!conflicting) unique.Add(sample);
            i = next;
        }
        return unique;
    }

    private static IReadOnlyList<SignalSample> ConsolidateSimultaneousSamples(
        IReadOnlyList<SignalSample> samples)
    {
        var unique = new List<SignalSample>(samples.Count);
        for (var i = 0; i < samples.Count;)
        {
            var sample = samples[i];
            var conflicting = !double.IsFinite(sample.Value);
            var next = i + 1;
            while (next < samples.Count &&
                   samples[next].TimestampNanoseconds == sample.TimestampNanoseconds)
            {
                if (!double.IsFinite(samples[next].Value) ||
                    samples[next].Value != sample.Value)
                    conflicting = true;
                next++;
            }
            // Keep an explicit nonnumeric timestamp marker for a conflict.
            // Simply deleting the group would incorrectly associate a DID
            // with a farther, unrelated CAN sample within the time window.
            unique.Add(conflicting
                ? sample with { Value = double.NaN } : sample);
            i = next;
        }
        return unique;
    }

    private static List<RawPair> Pair(
        IReadOnlyList<DiagnosticDidSample> didSamples,
        IReadOnlyList<SignalSample> signalSamples,
        long lag,
        long maxDistance)
    {
        var result = new List<RawPair>();
        // The DID observations and CAN timeline are already sorted. For a
        // dense diagnostic recording, advancing one CAN cursor is O(DID+CAN)
        // instead of doing an O(log CAN) binary search for every DID/lag.
        // Sparse DID polling against a huge high-frequency CAN trace still
        // uses binary search to avoid traversing millions of unrelated frames.
        var sequential = didSamples.Count > signalSamples.Count / 20;
        var cursor = 0;
        foreach (var did in didSamples)
        {
            // A signed nanosecond clock near Int64 limits must not wrap.
            long target;
            try { target = checked(did.TimestampNanoseconds + lag); }
            catch (OverflowException) { continue; }

            int index;
            if (sequential)
            {
                while (cursor < signalSamples.Count &&
                       signalSamples[cursor].TimestampNanoseconds < target)
                    cursor++;
                index = cursor;
            }
            else
            {
                index = LowerBound(signalSamples, target);
            }
            var hasBefore = index > 0;
            var hasAfter = index < signalSamples.Count;
            if (!hasBefore && !hasAfter) continue;

            var beforeDistance = hasBefore
                ? AbsoluteTimestampGap(signalSamples[index - 1].TimestampNanoseconds, target)
                : ulong.MaxValue;
            var afterDistance = hasAfter
                ? AbsoluteTimestampGap(signalSamples[index].TimestampNanoseconds, target)
                : ulong.MaxValue;
            var distance = Math.Min(beforeDistance, afterDistance);
            if (distance > (ulong)maxDistance) continue;

            // When two equally near CAN observations disagree, neither is
            // a defensible source for this UDS sample. Do not silently
            // choose whichever binary search happened to return first.
            if (hasBefore && hasAfter && beforeDistance == afterDistance &&
                signalSamples[index - 1].Value != signalSamples[index].Value)
                continue;

            var signal = beforeDistance <= afterDistance
                ? signalSamples[index - 1] : signalSamples[index];
            if (!double.IsFinite(signal.Value))
                continue; // nearest source timestamp was ambiguous
            result.Add(new RawPair(did, signal, (long)distance));
        }
        return result;
    }

    private static Fit FitValues(IReadOnlyList<RawPair> pairs, long lag, int totalDidSamples)
    {
        var xs = pairs.Select(pair => pair.Did.NumericValue!.Value).ToArray();
        var ys = pairs.Select(pair => pair.Signal.Value).ToArray();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var varianceX = xs.Sum(value => Square(value - meanX));
        var varianceY = ys.Sum(value => Square(value - meanY));
        var covariance = xs.Zip(ys).Sum(pair => (pair.First - meanX) * (pair.Second - meanY));
        var slope = varianceX <= 1e-18 ? 0 : covariance / varianceX;
        var intercept = meanY - slope * meanX;
        var predicted = xs.Select(value => slope * value + intercept).ToArray();
        var errors = predicted.Zip(ys).Select(pair => pair.Second - pair.First).ToArray();
        var mae = errors.Average(Math.Abs);
        var rmse = Math.Sqrt(errors.Average(Square));
        var sse = errors.Sum(Square);
        var bothConstant = varianceX <= 1e-18 && varianceY <= 1e-18;
        var rSquared = varianceY <= 1e-18 ? (bothConstant && rmse <= 1e-9 ? 1 : 0) : Math.Clamp(1 - sse / varianceY, 0, 1);
        var pearson = varianceX <= 1e-18 || varianceY <= 1e-18
            ? (bothConstant && rmse <= 1e-9 ? 1 : 0)
            : Math.Clamp(covariance / Math.Sqrt(varianceX * varianceY), -1, 1);
        var span = Math.Max(1e-9, ys.Max() - ys.Min());
        var errorScore = varianceY <= 1e-18 && varianceX > 1e-18 ? 0 : Math.Exp(-rmse / span * 3);
        var coverage = Math.Clamp((double)pairs.Count / Math.Max(1, totalDidSamples), 0, 1);
        var meanAlignment = pairs.Average(pair => (double)pair.AlignmentNanoseconds);
        var alignmentScore = Math.Exp(-meanAlignment / 100_000_000d);
        var points = pairs.Select((pair, index) => new CorrelationPoint(
            pair.Did.TimestampNanoseconds, pair.Signal.TimestampNanoseconds,
            pair.Did.NumericValue!.Value, pair.Signal.Value, predicted[index], errors[index],
            pair.Did.FrameIndex, pair.Signal.FrameIndex, pair.Did.Field,
            pair.Did.ResponseFrames)).ToArray();
        var score = (Math.Abs(pearson) * 0.35 + rSquared * 0.35 + errorScore * 0.30) * coverage * alignmentScore;
        return new Fit(lag, slope, intercept, pearson, rSquared, mae, rmse, errorScore,
            alignmentScore, meanAlignment, coverage, score, points);
    }

    private static ulong AbsoluteTimestampGap(long left, long right) =>
        left >= right
            ? unchecked((ulong)left - (ulong)right)
            : unchecked((ulong)right - (ulong)left);

    private static int LowerBound(IReadOnlyList<SignalSample> samples, long timestamp)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].TimestampNanoseconds < timestamp) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static UdsCanCorrelationResult Insufficient(CorrelationCandidate candidate, string reason) =>
        new(candidate, CorrelationVerdict.InsufficientData, candidate.Confidence * 0.35,
            candidate.Confidence, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, reason, []);

    private static double Square(double value) => value * value;
    private sealed record RawPair(DiagnosticDidSample Did, SignalSample Signal, long AlignmentNanoseconds);
    private sealed record Fit(
        long LagNanoseconds, double Slope, double Intercept, double Pearson, double RSquared,
        double Mae, double Rmse, double ErrorScore, double AlignmentScore, double MeanAlignmentNanoseconds,
        double Coverage, double Score,
        IReadOnlyList<CorrelationPoint> Points);
}
