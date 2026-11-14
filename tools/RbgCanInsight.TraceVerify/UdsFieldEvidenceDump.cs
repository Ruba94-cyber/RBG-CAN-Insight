using System.Globalization;
using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.TraceVerify;

/// <summary>
/// Optional, local-only audit export of DID subparameters proven against
/// the original ISO-TP CAN bytes. This is not a Vector-parity certificate.
/// All customer payload content remains in the operator's local CSV.
/// </summary>
public static class UdsFieldEvidenceDump
{
    public const string CsvHeader =
        "transaction,request_frame,response_first_frame,channel,request_id,response_id,extended," +
        "catalog_sha256,ecu_evidence,ecu,service_id,did,field_path,bit_offset,bit_length," +
        "raw,physical,unit,numeric_verified,source_spans";

    public sealed record Result(
        long SourceBackedRows,
        long UnverifiedRows,
        long Transactions,
        long UnresolvedDidResponses = 0);

    /// <summary>A strict audit must contain at least one verified field and
    /// no decoded field whose original CAN bytes could not be proven.
    /// A successful import alone is never proof of UDS coverage.</summary>
    public static bool HasCompleteVerifiedCoverage(Result? result) =>
        result is { SourceBackedRows: > 0, UnverifiedRows: 0, UnresolvedDidResponses: 0 };

    public static Result Write(
        TextWriter writer,
        IReadOnlyList<DiagnosticTransaction> transactions,
        string catalogSha256)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(transactions);
        if (catalogSha256.Length != 64 || catalogSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected SHA-256 hexadecimal catalog identity.",
                nameof(catalogSha256));

        var sourceRows = 0L;
        var unverifiedRows = 0L;
        var unresolvedDidResponses = 0L;
        writer.WriteLine(CsvHeader);
        foreach (var tx in transactions)
        {
            if (tx.Decoded.ServiceId == 0x22 &&
                (tx.Outcome is DiagnosticOutcome.Positive or DiagnosticOutcome.Malformed) &&
                (!tx.Decoded.DidPayloadFullyDecoded || tx.Decoded.Values.Count == 0))
                unresolvedDidResponses++;
            var response = tx.Responses.LastOrDefault(message =>
                message.Payload.Length > 0 && message.Payload[0] != 0x7F)
                ?? tx.Responses.LastOrDefault();
            foreach (var value in tx.Decoded.Values)
            {
                if (tx.Outcome != DiagnosticOutcome.Positive ||
                    response?.Payload.FirstOrDefault() != tx.Decoded.ServiceId + 0x40)
                {
                    unverifiedRows++;
                    continue;
                }
                if (!value.HasVerifiedSource || response is null ||
                    value.ResponseBitOffset is not { } startBit ||
                    value.ResponseBitLength is not { } bitLength)
                {
                    unverifiedRows++;
                    continue;
                }
                if (value.SourceSpans!.Any(span => span.FrameIndex < 0 ||
                    span.CanPayloadByteOffset < 0 ||
                    span.ResponsePayloadByteOffset < 0 || span.ByteCount <= 0))
                    throw new InvalidDataException("Invalid recorded source-byte interval.");

                // Textual values can originate in untrusted OEM definitions.
                // Escape RFC 4180 and prevent spreadsheet formula execution.
                string Field(int n) => n.ToString(CultureInfo.InvariantCulture);
                var rows = new[]
                {
                    Field(tx.Sequence),
                    Field(tx.Request?.FrameIndices.FirstOrDefault(-1) ?? -1),
                    Field(response.FrameIndices.FirstOrDefault(-1)),
                    Field(response.Channel),
                    Field((int) (tx.Request?.Identifier ?? 0u)),
                    Field((int)response.Identifier),
                    response.IsExtended ? "1" : "0",
                    catalogSha256.ToUpperInvariant(),
                    tx.Endpoint.Evidence.ToString(),
                    Safe(tx.Endpoint.Ecu),
                    Field(tx.Decoded.ServiceId),
                    Field(value.Did ?? -1),
                    Safe(value.Path),
                    Field(startBit),
                    Field(bitLength),
                    Safe(value.Raw),
                    Safe(value.Physical),
                    Safe(value.Unit),
                    value.NumericValue.HasValue ? "1" : "0",
                    Safe(string.Join(";", (value.SourceSpans ?? []).Select(span =>
                        $"{span.FrameIndex}:{span.CanPayloadByteOffset}:{span.ResponsePayloadByteOffset}:{span.ByteCount}")))
                };
                writer.WriteLine(string.Join(",", rows));
                sourceRows++;
            }
        }
        return new Result(sourceRows, unverifiedRows, transactions.Count,
            unresolvedDidResponses);
    }

    private static string Safe(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        // An authentic negative measurement is ordinary numeric data, not
        // a spreadsheet expression. Prefixing "-20" with an apostrophe
        // made our numeric_verified=1 CSV fail the independent float parser.
        // Text that starts with a formula operator (including "-cmd" or
        // "+SUM(...)") still gets an inert apostrophe.
        var isSafeNegativeNumber = value.StartsWith("-", StringComparison.Ordinal) &&
            double.TryParse(value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number);
        // A leading '+' is always escaped, even for +20, because Excel can
        // evaluate such text as a formula. Plain finite negative numbers
        // are allowed so a real physical -20 survives numeric comparison.
        if (!isSafeNegativeNumber && trimmed.Length > 0 &&
            trimmed[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
