using System.Text.RegularExpressions;
using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Tiny and transparent textual discovery aid, not a diagnostic ontology:
/// matching labels from an unverified catalog never proves a physical
/// dependency, equivalent DID identity or causal relation.
/// </summary>
public static class FaultSignalNameHints
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "fault", "error", "failure", "dtc", "test", "condition", "conditions",
        "signal", "detected", "active", "invalid", "valid", "plausibility",
        "control", "monitor", "monitoring", "system", "data", "value",
        "requested", "actual", "status", "circuit", "issue", "malfunction",
        "high", "low", "too", "out", "range", "sensor", "module", "the",
        "not", "for", "from", "with", "and", "of", "at", "is"
    };

    public static IReadOnlySet<string> FaultWords(FaultMemoryEntry fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        return Tokens(fault.Name + " " + fault.Description);
    }

    public static (int Score, string Explanation) Evaluate(
        IReadOnlySet<string> faultWords, SignalTimeline candidate)
    {
        ArgumentNullException.ThrowIfNull(faultWords);
        ArgumentNullException.ThrowIfNull(candidate);

        var exact = Tokens(candidate.Signal.Name).Where(faultWords.Contains)
            .OrderBy(word => word, StringComparer.Ordinal).ToArray();
        var message = Tokens(candidate.Message.Name).Where(word =>
            faultWords.Contains(word) && !exact.Contains(word, StringComparer.Ordinal))
            .OrderBy(word => word, StringComparer.Ordinal).ToArray();

        var score = 4 * exact.Length + message.Length;
        var summary = score == 0
            ? "Temporal proximity only; no DTC/DBC name match"
            : "Unverified DTC/DBC label overlap: " +
              string.Join(", ", exact.Concat(message).Distinct(StringComparer.Ordinal)) +
              " (discovery hint, NOT a proven fault dependency)";
        return (score, summary);
    }

    private static HashSet<string> Tokens(string text)
    {
        // "EngineSpeed" -> "Engine Speed", "ECMEngineTemp" -> "ECM Engine Temp".
        var separated = Regex.Replace(text, "(?<=[a-z0-9])(?=[A-Z])", " ");
        separated = Regex.Replace(separated, "(?<=[A-Z])(?=[A-Z][a-z])", " ");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(separated, "[A-Za-z0-9]+"))
        {
            var word = match.Value.ToLowerInvariant() switch
            {
                "rpm" or "revolutions" or "rev" => "speed",
                "temp" or "thermal" => "temperature",
                "eng" => "engine",
                "volt" or "volts" => "voltage",
                "batt" => "battery",
                "pres" => "pressure",
                _ => match.Value.ToLowerInvariant()
            };
            if (word.Length < 3 || Stopwords.Contains(word)) continue;
            result.Add(word);
        }
        return result;
    }
}
