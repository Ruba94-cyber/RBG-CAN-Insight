using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.Correlation;

/// <summary>One scored DID → CAN signal candidate with the evidence behind the score.</summary>
public sealed record CorrelationCandidate(
    DidParameter Did,
    DbcMessage Message,
    DbcSignal Signal,
    string Database,
    double Confidence,
    IReadOnlyList<string> Reasons)
{
    public string ReasonText => string.Join("; ", Reasons);

    public MappingStatus SuggestedStatus => Confidence switch
    {
        >= 0.70 => MappingStatus.Suggested,
        >= 0.45 => MappingStatus.ToVerify,
        _ => MappingStatus.Unmatched
    };
}

/// <summary>Options controlling the automatic DID ↔ signal correlation.</summary>
public sealed record CorrelationOptions(
    int MaxCandidatesPerDid = 3,
    double MinimumConfidence = 0.25,
    bool UseTraceEvidence = true);

/// <summary>
/// Offline correlation engine between diagnostic DIDs (from CDD/ODX) and CAN signals (from DBC).
/// Every available piece of documentation is scored — name/description similarity, unit, physical
/// range, scaling factor/offset, coded bit length — plus, when a trace is loaded, coherence
/// between the documented range and the values actually observed on the bus. The result is a
/// ranked list of candidates per DID with a human-readable justification for each score.
/// </summary>
public static class DidSignalCorrelator
{
    // Score weights: names decide the shortlist, metadata and trace evidence promote it.
    private const double NameWeight = 0.56;
    private const double UnitWeight = 0.14;
    private const double RangeWeight = 0.10;
    private const double ScalingWeight = 0.08;
    private const double BitLengthWeight = 0.04;
    private const double TraceWeight = 0.08;

    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "read", "data", "identifier", "did", "by", "current", "actual", "value", "parameter",
        "status", "state", "request", "response", "global", "local"
    };

    /// <summary>Convert an OEM DID with multiple independently declared
    /// fields into the exact field definition named by a decoded UDS response.
    /// This keeps field-specific unit, scale, bit length, name and range when
    /// searching CAN signals. Unknown or duplicate fields are intentionally
    /// unresolved rather than borrowing the DID-level metadata.</summary>
    public static DidParameter? ScopeToRecordedField(
        DidParameter did, string recordedFieldPath)
    {
        ArgumentNullException.ThrowIfNull(did);
        if (did.Fields.Count == 0) return did;
        if (string.IsNullOrWhiteSpace(recordedFieldPath)) return null;

        var matches = did.Fields.Where(field =>
            string.Equals(recordedFieldPath,
                $"DID {did.DidHex}/{field.Name}",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return null;
        var selected = matches[0];
        return did with
        {
            Name = selected.Name,
            LongName = selected.LongName,
            Description = selected.Description,
            Unit = selected.Unit,
            Minimum = selected.Minimum,
            Maximum = selected.Maximum,
            Factor = selected.Factor,
            Offset = selected.Offset,
            DataType = selected.DataType,
            BitLength = selected.BitLength,
            FieldDefinitions = [selected]
        };
    }

    public static IReadOnlyList<CorrelationCandidate> Correlate(
        DidParameter did,
        IReadOnlyList<DbcDatabase> databases,
    IReadOnlyDictionary<string, SignalChangeReport>? traceReports = null,
    IReadOnlyDictionary<int, DidTraceSummary>? didTraceSummaries = null,
    CorrelationOptions? options = null)
    {
        options ??= new CorrelationOptions();
        var candidates = new List<CorrelationCandidate>();

        foreach (var database in databases)
        {
            foreach (var message in database.AllMessageDefinitions)
            {
                foreach (var signal in message.Signals)
                {
                    var candidate = Score(did, database.Name, message, signal, traceReports, didTraceSummaries, options);
                    if (candidate.Confidence >= options.MinimumConfidence)
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Confidence)
            .Take(options.MaxCandidatesPerDid)
            .ToArray();
    }

    private static CorrelationCandidate Score(
        DidParameter did,
        string databaseName,
        DbcMessage message,
        DbcSignal signal,
        IReadOnlyDictionary<string, SignalChangeReport>? traceReports,
        IReadOnlyDictionary<int, DidTraceSummary>? didTraceSummaries,
        CorrelationOptions options)
    {
        var reasons = new List<string>();
        var score = 0d;

        // 1. Name / description similarity: compare DID text against signal, message and comments.
        var (nameScore, nameReason) = ComputeNameEvidence(did, message, signal);

        score += NameWeight * nameScore;
        if (nameScore >= 0.82)
        {
            reasons.Add(nameReason.Length == 0 ? $"strong name match ({nameScore:P0})" : nameReason);
        }
        else if (nameScore >= 0.48)
        {
            reasons.Add(nameReason.Length == 0 ? $"partial name match ({nameScore:P0})" : nameReason);
        }

        // 2. Unit.
        var didUnit = NormalizeUnit(did.Unit);
        var signalUnit = NormalizeUnit(signal.Unit);
        if (didUnit.Length > 0 && signalUnit.Length > 0)
        {
            if (didUnit == signalUnit)
            {
                score += UnitWeight;
                reasons.Add($"unit match ({signal.Unit})");
            }
            else
            {
                score -= 0.08; // documented units that disagree are meaningful negative evidence
                reasons.Add($"unit mismatch ({did.Unit} vs {signal.Unit})");
            }
        }

        // 3. Physical range overlap.
        if (did is { Minimum: { } didMin, Maximum: { } didMax } &&
            didMax > didMin && signal.Maximum > signal.Minimum &&
            !signal.PhysicalConversionUnsupported && !signal.UsesRangeDependentScale)
        {
            var overlap = Math.Min(didMax, signal.Maximum) - Math.Max(didMin, signal.Minimum);
            var union = Math.Max(didMax, signal.Maximum) - Math.Min(didMin, signal.Minimum);
            if (union > 0 && overlap > 0)
            {
                var ratio = overlap / union;
                score += RangeWeight * ratio;
                if (ratio >= 0.8)
                {
                    reasons.Add("physical range matches");
                }
                else if (ratio >= 0.3)
                {
                    reasons.Add("physical range overlaps");
                }
            }
        }

        // 4. Scaling: identical factor/offset is a strong hint that both describe the same encoding.
        if (!signal.PhysicalConversionUnsupported && !signal.UsesRangeDependentScale &&
            did.Factor is { } factor && factor != 0 && NearlyEqual(factor, signal.Factor))
        {
            score += ScalingWeight * (did.Offset is { } offset && NearlyEqual(offset, signal.Offset) ? 1 : 0.6);
            reasons.Add($"scaling factor matches ({signal.Factor.ToString(CultureInfo.InvariantCulture)})");
        }

        // 5. Coded size.
        if (did.BitLength is { } bits && bits == signal.BitLength)
        {
            score += BitLengthWeight;
            reasons.Add($"bit length matches ({bits} bit)");
        }

        // 6. Trace evidence: does the observed value range fit the documented DID range?
        if (options.UseTraceEvidence && traceReports is not null &&
            traceReports.TryGetValue(TraceReportKey(databaseName, message, signal), out var report) &&
            report.SampleCount > 0)
        {
            if (did is { Minimum: { } min, Maximum: { } max } && max > min)
            {
                var margin = (max - min) * 0.05;
                if (report.MinValue >= min - margin && report.MaxValue <= max + margin)
                {
                    score += TraceWeight;
                    reasons.Add("observed trace values inside documented range");
                }
            }
            else if (report.ChangeCount > 0)
            {
                score += TraceWeight * 0.3;
                reasons.Add("signal is alive in the loaded trace");
            }

            if (didTraceSummaries is not null &&
                didTraceSummaries.TryGetValue(did.Did, out var didSummary) &&
                didSummary.SampleCount > 0)
            {
                var overlap = Math.Min(didSummary.MaxValue, report.MaxValue) - Math.Max(didSummary.MinValue, report.MinValue);
                var union = Math.Max(didSummary.MaxValue, report.MaxValue) - Math.Min(didSummary.MinValue, report.MinValue);
                if (union <= 1e-9 && Math.Abs(didSummary.LastValue - report.LastValue) <= 1e-9)
                {
                    score += TraceWeight;
                    reasons.Add("UDS DID value observed in trace matches signal value");
                }
                else if (union > 0 && overlap > 0)
                {
                    var ratio = overlap / union;
                    score += TraceWeight * Math.Min(1, 0.4 + ratio);
                    reasons.Add($"UDS DID values observed in trace overlap signal range ({didSummary.SampleCount:N0} samples)");
                }
            }
        }

        if (nameScore < 0.25)
        {
            // Prevent "everything is 15%" style weak metadata-only candidates.
            var hasStrongMetadata = reasons.Any(reason =>
                reason.StartsWith("UDS", StringComparison.OrdinalIgnoreCase) ||
                reason.StartsWith("unit match", StringComparison.OrdinalIgnoreCase) ||
                reason.StartsWith("physical range matches", StringComparison.OrdinalIgnoreCase));
            score *= hasStrongMetadata ? 0.65 : 0.25;
        }

        if (reasons.Count == 0)
        {
            score = 0;
        }

        if (nameScore < 0.18 &&
            !reasons.Any(reason => reason.StartsWith("UDS", StringComparison.OrdinalIgnoreCase)))
        {
            score = 0;
        }

        return new CorrelationCandidate(
            did,
            message,
            signal,
            databaseName,
            Math.Clamp(score, 0, 1),
            reasons);
    }

    /// <summary>
    /// Key used to look up trace evidence for a signal. Includes the database and
    /// extended-ID bit to prevent collisions between different DBC definitions.
    /// </summary>
    public static string TraceReportKey(string databaseName, DbcMessage message, DbcSignal signal) =>
        $"{databaseName}:{message.Identifier}:{message.IsExtended}:{signal.Name}";

    private static (double Score, string Reason) ComputeNameEvidence(
        DidParameter did,
        DbcMessage message,
        DbcSignal signal)
    {
        var didTexts = new[]
        {
            did.Name,
            did.DisplayName,
            did.LongName,
            did.Description
        }.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Names and comments are scored separately so that, when a curated signal/message
        // comment is what actually explains the DID (the bare name would not have), the
        // "why this candidate" text can say so explicitly instead of a generic label.
        var nameTexts = new[] { signal.Name, message.Name, message.Transmitter }
            .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var commentTexts = new[] { signal.Comment, message.Comment }
            .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var signalTexts = nameTexts.Concat(commentTexts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var best = 0d;
        foreach (var left in didTexts)
        {
            foreach (var right in signalTexts)
            {
                best = Math.Max(best, NameSimilarity.Compute(left, right));
            }
        }

        var didTokens = didTexts.SelectMany(NameSimilarity.Tokenize)
            .Where(token => !GenericTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nameTokens = nameTexts.SelectMany(NameSimilarity.Tokenize)
            .Where(token => !GenericTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commentTokens = commentTexts.SelectMany(NameSimilarity.Tokenize)
            .Where(token => !GenericTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var signalTokens = nameTokens.Concat(commentTokens).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (didTokens.Count == 0 || signalTokens.Count == 0)
        {
            return (best, best >= 0.48 ? $"name similarity ({best:P0})" : string.Empty);
        }

        double TokenF1(HashSet<string> candidateTokens)
        {
            if (candidateTokens.Count == 0)
            {
                return 0;
            }

            var matchCount = didTokens.Count(candidateTokens.Contains);
            if (matchCount == 0)
            {
                return 0;
            }

            var candidateRecall = (double)matchCount / didTokens.Count;
            var candidatePrecision = (double)matchCount / candidateTokens.Count;
            return 2 * candidatePrecision * candidateRecall / (candidatePrecision + candidateRecall);
        }

        var intersection = didTokens.Count(token => signalTokens.Contains(token));
        var recall = (double)intersection / didTokens.Count;
        var precision = (double)intersection / signalTokens.Count;
        var tokenScore = intersection == 0 ? 0 : 2 * precision * recall / (precision + recall);
        var nameOnlyScore = TokenF1(nameTokens);
        var commentOnlyScore = TokenF1(commentTokens);

        // Strong bonus when a compact DBC abbreviation contains the meaningful DID tokens
        // after synonym expansion, e.g. VehicleSpeed -> VehSpd, CoolantTemperature -> CoolTemp.
        if (recall >= 0.8 && intersection >= Math.Min(2, didTokens.Count))
        {
            tokenScore = Math.Max(tokenScore, 0.88);
        }
        else if (recall >= 0.5 && intersection >= 2)
        {
            tokenScore = Math.Max(tokenScore, 0.68);
        }

        var final = Math.Max(best, tokenScore);
        var common = string.Join(", ", didTokens.Where(signalTokens.Contains).Take(4));

        // The signal/message name alone would not have explained this match — the curated
        // comment text is the deciding evidence, so surface that instead of a generic label.
        var commentDrivesMatch = commentOnlyScore >= 0.4 && commentOnlyScore > nameOnlyScore + 0.1;
        var reason = final switch
        {
            >= 0.82 when commentDrivesMatch => $"strong match via signal/message comment ({common})",
            >= 0.82 => $"strong token/name match ({common})",
            >= 0.48 when commentDrivesMatch => $"comment text explains this DID ({common})",
            >= 0.48 => $"partial token/name match ({common})",
            _ => string.Empty
        };
        return (final, reason);
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

    /// <summary>Shared conservative unit normalization for metadata and
    /// time-domain correlation. Do not promote incompatible physical units
    /// to confirmation merely because two trends happen to align.</summary>
    internal static string NormalizeUnit(string unit)
    {
        var trimmed = unit.Trim().ToLowerInvariant()
            .Replace("°", "deg")
            .Replace("percent", "%")
            .Replace(" ", string.Empty);
        return trimmed switch
        {
            "degc" or "celsius" or "c" => "degc",
            "degf" => "degf",
            "km/h" or "kmh" or "kph" => "km/h",
            "1/min" or "rpm" or "r/min" or "min-1" => "rpm",
            "kpa" or "kilopascal" => "kpa",
            "mbar" or "hpa" => "hpa",
            "v" or "volt" or "volts" => "v",
            "mv" => "mv",
            "a" or "amp" or "ampere" => "a",
            "ma" => "ma",
            "%" => "%",
            "nm" => "nm",
            "km" => "km",
            "m" => "m",
            "s" or "sec" or "seconds" => "s",
            "ms" => "ms",
            "h" or "hr" or "hours" => "h",
            "l" or "liter" or "litre" => "l",
            "l/h" => "l/h",
            "bar" => "bar",
            _ => trimmed
        };
    }
}
