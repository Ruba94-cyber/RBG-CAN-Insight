using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Correlation;

/// <summary>
/// Streams original paired UDS/CAN evidence into an RFC 4180 compatible CSV.
/// Displayed values are observations or fitted values with explicit labels:
/// this export cannot serve as automatic proof of signal identity.
/// </summary>
public static class CorrelationEvidenceCsvExporter
{
    public static int Write(TextWriter writer, UdsCanCorrelationResult correlation)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(correlation);

        writer.WriteLine(
            "Pair,DidHex,DidName,DidEcu,DidField,Dbc,CanChannel,CanIdHex,CanFrameFormat," +
            "Message,Signal,Verdict,EvidenceStatus,DidTimestampNs,CanTimestampNs," +
            "DidValue,CanValue,PredictedCanValue,Residual,DidFrameOneBased," +
            "CanFrameOneBased,IsoTpResponseFramesOneBased,StatisticalFitScore,AssociationIndicator,EvidenceKind");

        static string N(double value) =>
            value.ToString("G17", CultureInfo.InvariantCulture);

        static string Csv(string? text, bool sourceText = false)
        {
            text ??= string.Empty;
            // DBC/CDD/ODX names may originate in untrusted source files.
            // Prevent spreadsheet formula execution on opening the CSV.
            var firstMeaningful = text.AsSpan().TrimStart();
            if (sourceText && firstMeaningful.Length > 0 &&
                (firstMeaningful[0] is '=' or '+' or '-' or '@'))
                text = "'" + text;
            return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + text.Replace("\"", "\"\"") + "\""
                : text;
        }

        var evidence = correlation.HasSourcePairs
            ? "Observed paired CAN and UDS frames; statistical association only"
            : "No independently paired source evidence";
        var candidate = correlation.Candidate;
        // Only an observed, source-linked wire identity may appear as
        // the CSV's CAN ID. A PGN-matched DBC message can advertise a
        // completely different source address from the recorded ECU.
        var frameFormat = correlation.HasSourcePairs &&
                          correlation.SignalSourceIsExtended is { } extended
            ? extended ? "29-bit" : "11-bit"
            : "unverified";
        var canChannel = !correlation.HasSourcePairs || correlation.SignalChannel < 0
            ? "unverified" : $"CAN {correlation.SignalChannel + 1}";
        var sourceId = correlation.HasSourcePairs &&
                       correlation.SignalSourceIdentifier is { } recorded
            ? $"0x{recorded:X}" : "unverified";

        var count = 0;
        foreach (var point in correlation.Points)
        {
            count++;
            var isoTpFrames = point.DidResponseFrameIndices is { Count: > 0 }
                ? string.Join(";", point.DidResponseFrameIndices
                    .Where(index => index >= 0)
                    .Distinct()
                    .Select(index => (index + 1).ToString(CultureInfo.InvariantCulture)))
                : "";
            var cells = new[]
            {
                count.ToString(CultureInfo.InvariantCulture),
                candidate.Did.DidHex,
                Csv(candidate.Did.DisplayName, true),
                Csv(candidate.Did.Ecu, true),
                Csv(point.DidField, true),
                Csv(candidate.Database, true),
                Csv(canChannel, true),
                sourceId,
                frameFormat,
                Csv(candidate.Message.Name, true),
                Csv(candidate.Signal.Name, true),
                correlation.Verdict.ToString(),
                Csv(evidence, true),
                point.DidTimestampNanoseconds.ToString(CultureInfo.InvariantCulture),
                point.SignalTimestampNanoseconds.ToString(CultureInfo.InvariantCulture),
                N(point.DidValue),
                N(point.SignalValue),
                N(point.PredictedSignalValue),
                N(point.Error),
                point.DidFrameIndex < 0 ? "" :
                    (point.DidFrameIndex + 1).ToString(CultureInfo.InvariantCulture),
                point.SignalFrameIndex < 0 ? "" :
                    (point.SignalFrameIndex + 1).ToString(CultureInfo.InvariantCulture),
                Csv(isoTpFrames),
                N(correlation.StatisticalFitScore),
                N(correlation.AssociationConfidence),
                EvidenceKinds.Label(CorrelationEvidenceKinds.For(correlation))
            };
            writer.WriteLine(string.Join(",", cells));
        }
        return count;
    }
}
