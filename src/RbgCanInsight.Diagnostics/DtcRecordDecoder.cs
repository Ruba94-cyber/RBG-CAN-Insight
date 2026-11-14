using System.Globalization;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.Diagnostics;

public enum DtcRecordKind
{
    Snapshot,
    ExtendedData
}

/// <summary>One decoded field of a DTC snapshot or extended-data record.</summary>
public sealed record DecodedDtcRecordValue(
    string Name,
    string Raw,
    string Physical,
    string Unit,
    string Meaning,
    int? Did = null,
    double? NumericValue = null,
    int? ResponseBitOffset = null,
    int? ResponseBitLength = null,
    IReadOnlyList<DiagnosticSourceByteSpan>? SourceSpans = null)
{
    public bool HasVerifiedSource => ResponseBitOffset is >= 0 &&
        ResponseBitLength is > 0 && SourceSpans is { Count: > 0 };
    public string SourceEvidenceText => !HasVerifiedSource
        ? "Original CAN bytes not verified; inspect the raw 0x19 response."
        : $"UDS response bit {ResponseBitOffset}, length {ResponseBitLength} · " +
          string.Join("; ", SourceSpans!.Select(span => span.DisplayText));
}

/// <summary>A DTC snapshot (freeze-frame) or extended-data record taken from a 0x19 response.
/// <see cref="Note"/> is empty only when every byte of the record was explained by a catalog
/// definition; otherwise it says what could not be resolved and the raw bytes stay available.</summary>
public sealed record DecodedDtcRecord(
    int Code,
    byte? Status,
    DtcRecordKind Kind,
    int RecordNumber,
    string RawHex,
    IReadOnlyList<DecodedDtcRecordValue> Values,
    string Note,
    int? ResponseByteOffset = null,
    int? ResponseByteCount = null,
    IReadOnlyList<DiagnosticSourceByteSpan>? SourceSpans = null)
{
    public bool HasVerifiedSource => ResponseByteOffset is >= 0 &&
        ResponseByteCount is > 0 && SourceSpans is { Count: > 0 };
    public string SourceEvidenceText => !HasVerifiedSource
        ? "Record byte source unverified"
        : string.Join("; ", SourceSpans!.Select(span => span.DisplayText));
    public string KindText => Kind == DtcRecordKind.Snapshot ? "snapshot" : "extended data";
    public bool IsFullyDecoded => Note.Length == 0 && Values.Count > 0;

    public string SummaryText
    {
        get
        {
            var head = string.Create(CultureInfo.InvariantCulture, $"{KindText} record 0x{RecordNumber:X2}");
            var body = Values.Count == 0
                ? RawHex
                : string.Join("; ", Values.Select(value =>
                    string.IsNullOrWhiteSpace(value.Physical)
                        ? $"{value.Name} = {value.Raw}"
                        : $"{value.Name} = {value.Physical}{(string.IsNullOrWhiteSpace(value.Unit) ? string.Empty : " " + value.Unit)}"));
            return Note.Length == 0 ? $"{head}: {body}" : $"{head}: {body} ({Note})";
        }
    }
}

/// <summary>Decodes the record payloads of ISO 14229-1 ReadDTCInformation responses
/// (sub-functions 0x04, 0x05, 0x06 and 0x10) with catalog definitions. Layouts used:
/// <list type="bullet">
/// <item>0x04: DTC(3) status record{number, identifierCount, {DID(2), data}*}*</item>
/// <item>0x05: record{number, DTC(3), status, identifierCount, {DID(2), data}*}*</item>
/// <item>0x06 / 0x10: DTC(3) status record{number, data}*</item>
/// </list>
/// Snapshot DIDs are split with the DID width declared by the catalog. Extended-data records have
/// no length field, so a response that holds several records can only be split when the request
/// named one record number; otherwise the block stays raw. DTC-level snapshot/extended field
/// definitions of a catalog are applied to the bytes that follow the record number and are only
/// used when DID-structured decoding is not possible.</summary>
public static class DtcRecordDecoder
{
    public static bool IsRecordSubFunction(byte subFunction) => subFunction is 0x04 or 0x05 or 0x06 or 0x10;

    public static IReadOnlyList<DecodedDtcRecord> Decode(
        byte[] response, byte[] request, IReadOnlyList<DidCatalog> catalogs, string? ecu)
    {
        if (response.Length < 3 || response[0] != 0x59) return [];
        var sub = (byte)(response[1] & 0x7F);
        var requestedRecord = request.Length >= 6 && request[0] == 0x19 && (request[1] & 0x7F) == sub
            ? (int?)request[5]
            : sub == 0x05 && request.Length >= 3 && request[0] == 0x19 ? request[2] : null;
        var allRecords = requestedRecord is null or 0xFF or 0xFE;
        return sub switch
        {
            0x04 => DecodeSnapshotByDtc(response, allRecords, catalogs, ecu),
            0x05 => DecodeStoredDataByRecord(response, catalogs, ecu),
            0x06 or 0x10 => DecodeExtended(response, requestedRecord, catalogs, ecu),
            _ => []
        };
    }

    private static IReadOnlyList<DecodedDtcRecord> DecodeSnapshotByDtc(
        byte[] data, bool allRecords, IReadOnlyList<DidCatalog> catalogs, string? ecu)
    {
        if (data.Length < 6) return [];
        var code = (data[2] << 16) | (data[3] << 8) | data[4];
        var status = data[5];
        var records = new List<DecodedDtcRecord>();
        var position = 6;
        while (position < data.Length)
        {
            var start = position;
            var recordNumber = data[position++];
            records.Add(ParseSnapshotBody(data, start, recordNumber, ref position, code, status, allRecords, catalogs, ecu));
        }
        return records;
    }

    private static IReadOnlyList<DecodedDtcRecord> DecodeStoredDataByRecord(
        byte[] data, IReadOnlyList<DidCatalog> catalogs, string? ecu)
    {
        var records = new List<DecodedDtcRecord>();
        var position = 2;
        while (position + 5 <= data.Length)
        {
            var start = position;
            var recordNumber = data[position];
            var code = (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
            var status = data[position + 4];
            position += 5;
            records.Add(ParseSnapshotBody(data, start, recordNumber, ref position, code, status, true, catalogs, ecu));
        }
        return records;
    }

    /// <summary>Parses "identifierCount, {DID, data}*" starting at <paramref name="position"/>.</summary>
    private static DecodedDtcRecord ParseSnapshotBody(
        byte[] data, int start, int recordNumber, ref int position, int code, byte status,
        bool otherRecordsMayFollow, IReadOnlyList<DidCatalog> catalogs, string? ecu)
    {
        if (position >= data.Length)
            return Make(code, status, recordNumber, [], "record has no identifier count",
                UdsServiceDecoder.FormatHex(data.AsSpan(start, position - start)),
                responseByteOffset: start, responseByteCount: position - start);
        var identifierCount = data[position++];
        var values = new List<DecodedDtcRecordValue>();
        var notes = new List<string>();
        for (var index = 0; index < identifierCount; index++)
        {
            if (position + 2 > data.Length)
            {
                notes.Add($"truncated before identifier {index + 1} of {identifierCount}");
                break;
            }
            var did = (data[position] << 8) | data[position + 1];
            var definition = UdsServiceDecoder.FindUniqueDid(did, catalogs, ecu);
            var widthBytes = DeclaredWidthBytes(definition);
            var available = data.Length - (position + 2);
            if (widthBytes is null)
            {
                // The width is unknown: only a single identifier in a response that cannot hold
                // further records is delimited by the end of the payload.
                if (identifierCount == 1 && !otherRecordsMayFollow)
                    widthBytes = available;
                else
                {
                    var rest = data.AsSpan(position + 2);
                    values.Add(new DecodedDtcRecordValue(
                        UdsServiceDecoder.DidText(did, catalogs, ecu), UdsServiceDecoder.FormatHex(rest),
                        string.Empty, string.Empty, string.Empty, did, null,
                        (position + 2) * 8, rest.Length * 8));
                    notes.Add($"DID 0x{did:X4} has no declared width; remaining bytes kept raw");
                    position = data.Length;
                    break;
                }
            }
            if (widthBytes > available || widthBytes <= 0)
            {
                notes.Add($"DID 0x{did:X4} needs {widthBytes} byte(s) but only {available} remain");
                values.Add(new DecodedDtcRecordValue(
                    UdsServiceDecoder.DidText(did, catalogs, ecu),
                    UdsServiceDecoder.FormatHex(data.AsSpan(position + 2)), string.Empty, string.Empty, string.Empty, did, null,
                    (position + 2) * 8, available * 8));
                position = data.Length;
                break;
            }
            var raw = data.AsSpan(position + 2, widthBytes.Value);
            AddDidValues(values, did, raw, definition, catalogs, ecu, notes,
                responseDataByteOffset: position + 2);
            position += 2 + widthBytes.Value;
        }

        // DTC-level definitions can describe an OEM snapshot that is not DID-structured.
        if (values.Count == 0 && identifierCount == 0 && position <= data.Length)
        {
            var dtc = UdsServiceDecoder.FindUniqueDtc(code, catalogs, ecu);
            if (dtc is { SnapshotFields.Count: > 0 } && !otherRecordsMayFollow)
            {
                var body = data.AsSpan(position);
                ApplyDtcFields(values, dtc.SnapshotFields, body, notes,
                    responseDataByteOffset: position);
                position = data.Length;
            }
        }

        var rawHex = UdsServiceDecoder.FormatHex(data.AsSpan(start, Math.Max(0, position - start)));
        return Make(code, status, recordNumber, values, string.Join("; ", notes), rawHex,
            responseByteOffset: start, responseByteCount: position - start);
    }

    private static IReadOnlyList<DecodedDtcRecord> DecodeExtended(
        byte[] data, int? requestedRecord, IReadOnlyList<DidCatalog> catalogs, string? ecu)
    {
        if (data.Length < 7) return [];
        var code = (data[2] << 16) | (data[3] << 8) | data[4];
        var status = data[5];
        var dtc = UdsServiceDecoder.FindUniqueDtc(code, catalogs, ecu);
        var number = data[6];
        var body = data.AsSpan(7);
        var values = new List<DecodedDtcRecordValue>();
        var notes = new List<string>();
        var multiple = requestedRecord is null or 0xFF or 0xFE;
        var definedBytes = dtc is { ExtendedDataFields.Count: > 0 } && dtc.ExtendedDataFields.All(f => f.BitOffset >= 0 && f.BitLength > 0)
            ? (dtc.ExtendedDataFields.Max(f => f.BitOffset + f.BitLength) + 7) / 8
            : -1;
        if (multiple && definedBytes != body.Length)
        {
            notes.Add("request asked for several records and extended data has no length field; record boundaries unresolved");
            return [Make(code, status, number, values, string.Join("; ", notes),
                UdsServiceDecoder.FormatHex(data.AsSpan(6)), DtcRecordKind.ExtendedData,
                responseByteOffset: 6, responseByteCount: data.Length - 6)];
        }

        if (dtc is { ExtendedDataFields.Count: > 0 })
            ApplyDtcFields(values, dtc.ExtendedDataFields, body, notes,
                responseDataByteOffset: 7);
        else
            notes.Add("no extended-data definition in the loaded catalog");
        return [Make(code, status, number, values, string.Join("; ", notes),
            UdsServiceDecoder.FormatHex(data.AsSpan(6)), DtcRecordKind.ExtendedData,
            responseByteOffset: 6, responseByteCount: data.Length - 6)];
    }

    private static void ApplyDtcFields(
        List<DecodedDtcRecordValue> values, IReadOnlyList<DiagnosticFieldDefinition> fields,
        ReadOnlySpan<byte> body, List<string> notes, int responseDataByteOffset)
    {
        var valueStart = values.Count;
        var coveredIntervals = new List<(int Start, int End)>();
        var undecodable = 0;
        foreach (var field in fields)
        {
            if (!UdsServiceDecoder.TryDecodeField(body, field, out var raw, out var numeric, out var physical, out var meaning))
            {
                undecodable++;
                continue;
            }
            values.Add(new DecodedDtcRecordValue(field.DisplayName, raw, physical,
                field.Unit, meaning, null, numeric,
                responseDataByteOffset * 8 + field.BitOffset, field.BitLength));
            coveredIntervals.Add((field.BitOffset, field.BitOffset + field.BitLength));
        }
        if (undecodable > 0)
            notes.Add($"{undecodable} declared field(s) could not be decoded from {body.Length} record byte(s)");
        // A declaration for the last field does not mean earlier bytes or
        // reserved bit gaps are explained. Union the successful, exact bit
        // intervals before claiming an entire OEM DTC record was decoded.
        var cursor = 0L;
        var uncovered = 0L;
        var overlapping = false;
        foreach (var (start, end) in coveredIntervals.OrderBy(range => range.Start))
        {
            if (start < cursor) overlapping = true;
            if (start > cursor) uncovered += start - cursor;
            cursor = Math.Max(cursor, end);
        }
        if (overlapping)
        {
            notes.Add("overlapping CDD/ODX field definitions; record layout is ambiguous");
            InvalidateAmbiguousValues(values, valueStart);
        }
        var totalBits = (long)body.Length * 8;
        if (cursor < totalBits) uncovered += totalBits - cursor;
        if (uncovered > 0)
            notes.Add($"{uncovered} bit(s) are not covered by decoded CDD/ODX fields; raw record retained");
    }

    private static void AddDidValues(
        List<DecodedDtcRecordValue> values, int did, ReadOnlySpan<byte> raw, DidParameter? definition,
        IReadOnlyList<DidCatalog> catalogs, string? ecu, List<string> notes,
        int responseDataByteOffset)
    {
        var label = UdsServiceDecoder.DidText(did, catalogs, ecu);
        if (definition is null)
        {
            values.Add(new DecodedDtcRecordValue(label, UdsServiceDecoder.FormatHex(raw),
                string.Empty, string.Empty, string.Empty, did, null,
                responseDataByteOffset * 8, raw.Length * 8));
            notes.Add($"DID 0x{did:X4} is not defined in the loaded catalog");
            return;
        }
        if (definition.EngineeringIssues.Count > 0)
        {
            values.Add(new DecodedDtcRecordValue(label, UdsServiceDecoder.FormatHex(raw), string.Empty, definition.Unit,
                "Conflicting CDD/ODX DID layout: raw code only.", did, null,
                responseDataByteOffset * 8, raw.Length * 8));
            notes.Add($"DID 0x{did:X4} has conflicting catalog definitions");
            return;
        }
        if (definition.Fields.Count > 0)
        {
            var valueStart = values.Count;
            var undecodedFields = 0;
            var decodedIntervals = new List<(int Start, int End)>();
            foreach (var field in definition.Fields)
            {
                if (!UdsServiceDecoder.TryDecodeField(raw, field, out var rawText, out var numeric, out var physical, out var meaning))
                {
                    undecodedFields++;
                    continue;
                }
                values.Add(new DecodedDtcRecordValue($"{label}/{field.DisplayName}", rawText,
                    physical, field.Unit, meaning, did, numeric,
                    responseDataByteOffset * 8 + field.BitOffset, field.BitLength));
                decodedIntervals.Add((field.BitOffset, field.BitOffset + field.BitLength));
            }
            if (undecodedFields > 0)
                notes.Add($"DID 0x{did:X4} has {undecodedFields} unreadable subfield(s); raw record retained");
            // Successful decoding of one subfield does not establish that
            // every bit in the snapshot DID was explained. This matters for
            // CDD/ODX definitions containing reserved bytes or bit gaps.
            var cursor = 0L;
            var uncovered = 0L;
            var overlapping = false;
            foreach (var (start, end) in decodedIntervals.OrderBy(interval => interval.Start))
            {
                if (start < cursor) overlapping = true;
                if (start > cursor) uncovered += start - cursor;
                cursor = Math.Max(cursor, end);
            }
            if (overlapping)
            {
                notes.Add($"DID 0x{did:X4} has overlapping CDD/ODX field definitions; decoded values are ambiguous");
                InvalidateAmbiguousValues(values, valueStart);
            }
            var totalBits = (long)raw.Length * 8;
            if (cursor < totalBits) uncovered += totalBits - cursor;
            if (uncovered > 0)
                notes.Add($"DID 0x{did:X4} has {uncovered} bit(s) not covered by decoded CDD/ODX fields; raw record retained");
            return;
        }

        var text = UdsServiceDecoder.DecodePhysical(raw, definition);
        double? parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                         double.IsFinite(number) && definition.BitLength is > 0 &&
                         !definition.DataType.Contains("ASCII", StringComparison.OrdinalIgnoreCase)
            ? number : null;
        values.Add(new DecodedDtcRecordValue(label, UdsServiceDecoder.FormatHex(raw),
            text, definition.Unit, definition.Description, did, parsed,
            responseDataByteOffset * 8,
            definition.BitLength is > 0 ? definition.BitLength : raw.Length * 8));
    }

    private static int? DeclaredWidthBytes(DidParameter? definition)
    {
        if (definition is null) return null;
        int? bits;
        if (definition.Fields.Count > 0)
            bits = definition.Fields.All(f => f.BitOffset >= 0 && f.BitLength > 0 && (long)f.BitOffset + f.BitLength <= int.MaxValue)
                ? definition.Fields.Max(f => f.BitOffset + f.BitLength)
                : null;
        else
            bits = definition.BitLength;
        return bits is > 0 ? (bits.Value + 7) / 8 : null;
    }

    /// <summary>Keep exact original bytes inspectable but do not turn a
    /// contradictory CDD/ODX bit layout into physical values or numeric evidence.</summary>
    private static void InvalidateAmbiguousValues(
        List<DecodedDtcRecordValue> values, int startIndex)
    {
        for (var index = startIndex; index < values.Count; index++)
        {
            var value = values[index];
            values[index] = value with
            {
                Physical = value.Raw,
                NumericValue = null,
                Meaning = "Overlapping CDD/ODX field definitions; raw code only, physical conversion withheld."
            };
        }
    }

    private static DecodedDtcRecord Make(
        int code, byte status, int recordNumber, IReadOnlyList<DecodedDtcRecordValue> values,
        string note, string rawHex = "", DtcRecordKind kind = DtcRecordKind.Snapshot,
        int responseByteOffset = -1, int responseByteCount = -1) =>
        new(code, status, kind, recordNumber, rawHex, values, note,
            responseByteOffset >= 0 ? responseByteOffset : null,
            responseByteCount >= 0 ? responseByteCount : null);
}
