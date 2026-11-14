using System.Globalization;
using System.Numerics;

namespace RbgCanInsight.Dbc;

/// <summary>One physical payload bit, numbered by byte and LSB 0..7. DBC start-bit
/// positions retain Vector's sawtooth numbering for Motorola (@0) signals.</summary>
public sealed record DbcBitCell(
    int ByteIndex,
    int BitNumber,
    int AbsoluteBitNumber,
    bool InSelectedSignal,
    bool IsSignalStart,
    int? LogicalSignalBit,
    IReadOnlyList<string> OtherSignals,
    int? RecordedValue = null)
{
    public bool IsOccupied => InSelectedSignal || OtherSignals.Count > 0;
    public bool IsOverlapping => InSelectedSignal && OtherSignals.Count > 0;
    public string Glyph => IsSignalStart ? "S" : InSelectedSignal ? "■" :
        OtherSignals.Count > 0 ? "□" : "·";
    public string RecordedBitText => RecordedValue is { } value ? value.ToString() : "·";
    public string Tooltip
    {
        get
        {
            var result = $"Byte {ByteIndex} · bit {BitNumber} · DBC bit {AbsoluteBitNumber}";
            if (LogicalSignalBit is { } bit)
                result += $" · selected signal value bit {bit}" +
                          (IsSignalStart ? " · DBC start bit" : string.Empty);
            if (OtherSignals.Count > 0)
                result += $" · also occupied by: {string.Join(", ", OtherSignals)}";
            if (RecordedValue is { } recorded)
                result += $" · recorded raw bit: {recorded}";
            else
                result += " · no source payload bit available";
            return result;
        }
    }
}

public sealed record DbcBitRow(int ByteIndex, IReadOnlyList<DbcBitCell> Cells)
{
    public string ByteLabel => $"Byte {ByteIndex:D2}";
    public string HexOffset => $"0x{ByteIndex:X2}";
}

public sealed record DbcNamedValue(long Raw, string Meaning, double Physical, string Unit)
{
    public string RawHex => $"0x{Raw:X}";
    public string PhysicalText => !double.IsFinite(Physical)
        ? "RAW ONLY (no verified conversion)"
        : Physical.ToString("G9", CultureInfo.InvariantCulture) +
          (string.IsNullOrWhiteSpace(Unit) ? string.Empty : " " + Unit);
}

public sealed record DbcSignalLayout(
    DbcMessage Message,
    DbcSignal Signal,
    IReadOnlyList<DbcBitRow> Rows,
    IReadOnlyList<DbcNamedValue> NamedValues,
    IReadOnlyList<int> PhysicalBitPositions,
    IReadOnlyList<string> Warnings)
{
    public int OccupiedBitCount => PhysicalBitPositions.Count;
    public string ByteOrderText => Signal.ByteOrder == DbcByteOrder.Intel
        ? "Intel / little-endian (DBC @1)" : "Motorola / big-endian (DBC @0)";
    public string EncodingText => Signal.ValueType switch
    {
        DbcSignalValueType.Float32 => "IEEE-754 float32",
        DbcSignalValueType.Float64 => "IEEE-754 float64",
        _ => Signal.IsSigned ? "Signed integer (two's complement)" : "Unsigned integer"
    };
    public string CodedRangeText
    {
        get
        {
            if (Signal.ValueType == DbcSignalValueType.Float32 || Signal.ValueType == DbcSignalValueType.Float64)
                return "Raw encoding: IEEE-754 floating point; no integer code range.";
            if (Signal.BitLength is <= 0 or > 64)
                return "Raw code range not representable with this signal length.";
            var bits = Signal.BitLength;
            var max = BigInteger.Pow(2, Signal.IsSigned ? bits - 1 : bits) - 1;
            var min = Signal.IsSigned ? -BigInteger.Pow(2, bits - 1) : BigInteger.Zero;
            return $"Raw representable codes: {min} .. {max} " +
                   $"({bits}-bit {(Signal.IsSigned ? "signed" : "unsigned")} integer).";
        }
    }

    public string SelectedBoundsText
    {
        get
        {
            if (PhysicalBitPositions.Count == 0) return "Not mapped";
            var start = PhysicalBitPositions[0];
            var end = PhysicalBitPositions[^1];
            return $"DBC start {Signal.StartBit}; {Signal.BitLength} bits; " +
                   $"byte {start / 8}, bit {start % 8} → byte {end / 8}, bit {end % 8}";
        }
    }

    public string FormulaText => Signal.PhysicalConversionUnsupported
        ? "Physical conversion unsupported: raw only"
        : Signal.ScaleSegments is { Count: > 0 } segments
            ? "Source-dependent scaling: " + string.Join("; ",
                segments.Select(segment =>
                    $"[{segment.RawLower?.ToString("G9", CultureInfo.InvariantCulture) ?? "-∞"}, " +
                    $"{segment.RawUpper?.ToString("G9", CultureInfo.InvariantCulture) ?? "+∞"}] " +
                    $"raw × {segment.Factor.ToString("G9", CultureInfo.InvariantCulture)} " +
                    $"+ {segment.Offset.ToString("G9", CultureInfo.InvariantCulture)}"))
            : $"physical = raw × {Signal.Factor.ToString("G9", CultureInfo.InvariantCulture)} " +
              $"{(Signal.Offset < 0 ? "−" : "+")} {Math.Abs(Signal.Offset).ToString("G9", CultureInfo.InvariantCulture)}";
    public string RangeText => $"{Signal.Minimum.ToString("G9", CultureInfo.InvariantCulture)} .. " +
        $"{Signal.Maximum.ToString("G9", CultureInfo.InvariantCulture)}" +
        (string.IsNullOrWhiteSpace(Signal.Unit) ? string.Empty : " " + Signal.Unit);
    public string MultiplexText => Signal.MultiplexConditions is { Count: > 0 } conditions
        ? "Active when " + string.Join(" AND ",
            conditions.GroupBy(condition => condition.Multiplexor).Select(group =>
                group.Key + " in [" + string.Join(", ",
                    group.SelectMany(item => item.Ranges).Select(range =>
                        range.Minimum == range.Maximum ? $"{range.Minimum}" : $"{range.Minimum}-{range.Maximum}")) + "]"))
        : Signal.IsMultiplexor && Signal.MultiplexValue is { } selector
            ? $"Nested multiplexor at parent selector value {selector}"
        : Signal.IsMultiplexor ? "Multiplexor / selector" :
        Signal.MultiplexValue is { } branch ? $"Active when multiplexor = {branch}" :
        "Always present (not multiplexed)";
}

/// <summary>Lossless raw extraction from the selected source payload.
/// The raw hexadecimal/integer text is exact; physical scaling through double
/// may be approximate. For IEEE-754 signals the hex is a bit pattern, not an
/// integer code and cannot be looked up as a DBC VAL_ enumeration.</summary>
public sealed record DbcRecordedRawValue(
    ulong RawBits,
    string RawHex,
    string RawText,
    bool IsExactInDouble,
    bool IsFloatingPoint,
    string? DeclaredMeaning);

/// <summary>
/// Pure, deterministic signal-layout inspector shared by WPF and tests. It never
/// guesses from the captured payload, and does not collapse Motorola sawtooth
/// numbering into the Intel numbering convention.
/// </summary>
public static class DbcSignalInspector
{
    public static DbcSignalLayout Inspect(DbcMessage message, DbcSignal selected)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(selected);

        var warnings = new List<string>();
        if (message.Length is < 0 or > 64)
            warnings.Add($"DBC message declares {message.Length} data bytes; the offline CAN/CAN-FD layout displays up to 64 bytes.");

        var dataBytes = Math.Clamp(message.Length, 0, 64);
        var signalBits = PhysicalBits(selected).ToArray();
        var bitToLogical = new Dictionary<int, int>();
        for (var index = 0; index < signalBits.Length; index++)
        {
            bitToLogical[signalBits[index]] = selected.ByteOrder == DbcByteOrder.Intel
                ? index : selected.BitLength - 1 - index;
        }

        if (selected.BitLength is <= 0 or > 64)
            warnings.Add("DBC bit length is outside the supported 1–64-bit scalar range.");
        if (signalBits.Length != selected.BitLength)
            warnings.Add("Some positions could not be constructed from the declared DBC bit length.");
        if (signalBits.Any(bit => bit < 0 || bit >= dataBytes * 8))
            warnings.Add("The selected signal extends outside its declared message DLC.");
        if (selected.ValueType == DbcSignalValueType.Float32 && selected.BitLength != 32 ||
            selected.ValueType == DbcSignalValueType.Float64 && selected.BitLength != 64)
            warnings.Add("Floating-point encoding and declared signal bit length do not agree.");
        if (selected.ValueType == DbcSignalValueType.Integer && selected.BitLength > 53)
            warnings.Add("Wide integer signal: double-based time-series and physical " +
                "values cannot represent every raw code above 2^53-1 exactly; " +
                "use source raw bits and integer-coded descriptions for exact comparisons.");
        if (selected.PhysicalConversionUnsupported)
            warnings.Add("Unsupported ARXML physical conversion: raw value only.");
        else if (selected.UsesRangeDependentScale)
            warnings.Add("ARXML interval-specific conversion: uncovered or conflicting raw codes have no verified physical value.");

        var occupiedByOther = new Dictionary<int, List<string>>();
        foreach (var other in message.Signals.Where(signal => !ReferenceEquals(signal, selected)))
        {
            foreach (var bit in PhysicalBits(other))
            {
                if (bit < 0 || bit >= dataBytes * 8) continue;
                if (!occupiedByOther.TryGetValue(bit, out var names))
                {
                    names = [];
                    occupiedByOther[bit] = names;
                }
                names.Add(other.Name);
            }
        }

        if (signalBits.Any(bit => occupiedByOther.ContainsKey(bit)))
            warnings.Add("Some bits are also used by another DBC signal. This can be valid in different multiplexing branches; check selector conditions before reporting an overlap as a defect.");

        var rows = new List<DbcBitRow>(dataBytes);
        for (var byteIndex = 0; byteIndex < dataBytes; byteIndex++)
        {
            var cells = new List<DbcBitCell>(8);
            for (var bit = 7; bit >= 0; bit--)
            {
                var physicalBit = byteIndex * 8 + bit;
                var selectedBit = bitToLogical.TryGetValue(physicalBit, out var logicalBit);
                cells.Add(new DbcBitCell(
                    byteIndex, bit, physicalBit, selectedBit,
                    selectedBit && physicalBit == selected.StartBit,
                    selectedBit ? logicalBit : null,
                    occupiedByOther.TryGetValue(physicalBit, out var names) ? names : []));
            }
            rows.Add(new DbcBitRow(byteIndex, cells));
        }

        var named = (selected.ValueDescriptions ?? new Dictionary<long, string>())
            .OrderBy(pair => pair.Key)
            .Select(pair => new DbcNamedValue(pair.Key, pair.Value,
                selected.TryGetPhysicalValue(pair.Key, out var physical) ? physical : double.NaN,
                selected.Unit))
            .ToArray();
        return new DbcSignalLayout(message, selected, rows, named, signalBits, warnings);
    }

    /// <summary>Extract the DBC-coded value directly from the recorded
    /// bytes without an intermediate double or an inferred missing bit.
    /// Returns null when the actual frame is too short or the definition is
    /// outside the supported scalar range.</summary>
    public static DbcRecordedRawValue? InspectRecordedRaw(
        DbcSignal signal, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.BitLength is < 1 or > 64 || signal.StartBit < 0)
            return null;
        if (signal.ValueType == DbcSignalValueType.Float32 && signal.BitLength != 32 ||
            signal.ValueType == DbcSignalValueType.Float64 && signal.BitLength != 64)
            return null;

        var positions = PhysicalBits(signal);
        // ReadOnlySpan<T> is a ref struct and cannot be captured in a LINQ
        // lambda. Capture the scalar length instead (also cheaper to check).
        var payloadBytes = payload.Length;
        if (positions.Count != signal.BitLength ||
            positions.Any(position => position < 0 ||
                position / 8 >= payloadBytes))
            return null;

        ulong bits = 0;
        if (signal.ByteOrder == DbcByteOrder.Intel)
        {
            for (var index = 0; index < positions.Count; index++)
            {
                var source = positions[index];
                var bit = (payload[source / 8] >> (source % 8)) & 1;
                bits |= (ulong)bit << index;
            }
        }
        else
        {
            foreach (var source in positions)
            {
                var bit = (payload[source / 8] >> (source % 8)) & 1;
                bits = (bits << 1) | (uint)bit;
            }
        }

        var digitCount = (signal.BitLength + 3) / 4;
        var rawHex = "0x" + bits.ToString("X" +
            digitCount.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
        if (signal.ValueType != DbcSignalValueType.Integer)
            return new DbcRecordedRawValue(bits, rawHex,
                "IEEE-754 bit pattern", false, true, null);

        const ulong MaxUnambiguousDoubleInteger = 9007199254740991UL;
        if (!signal.IsSigned)
        {
            return new DbcRecordedRawValue(bits, rawHex,
                bits.ToString(CultureInfo.InvariantCulture),
                bits <= MaxUnambiguousDoubleInteger, false,
                bits <= long.MaxValue
                    ? signal.GetValueDescription((long)bits) : null);
        }

        long signed = signal.BitLength == 64
            ? unchecked((long)bits)
            : (long)((bits ^ (1UL << (signal.BitLength - 1))) -
                (1UL << (signal.BitLength - 1)));
        return new DbcRecordedRawValue(bits, rawHex,
            signed.ToString(CultureInfo.InvariantCulture),
            signed >= -(long)MaxUnambiguousDoubleInteger &&
            signed <= (long)MaxUnambiguousDoubleInteger, false,
            signal.GetValueDescription(signed));
    }

    public static IReadOnlyList<DbcBitRow> OverlayRecordedPayload(
        DbcSignalLayout layout, ReadOnlySpan<byte> payload)
    {
        var rows = new List<DbcBitRow>(layout.Rows.Count);
        foreach (var row in layout.Rows)
        {
            var cells = new List<DbcBitCell>(row.Cells.Count);
            foreach (var cell in row.Cells)
            {
                int? recorded = cell.ByteIndex < payload.Length
                    ? (payload[cell.ByteIndex] >> cell.BitNumber) & 1
                    : null;
                cells.Add(cell with { RecordedValue = recorded });
            }
            rows.Add(new DbcBitRow(row.ByteIndex, cells));
        }
        return rows;
    }

    /// <summary>Returns bit positions in raw value extraction order: Intel from
    /// least to most significant; Motorola from most to least significant.</summary>
    public static IReadOnlyList<int> PhysicalBits(DbcSignal signal)
    {
        if (signal.StartBit < 0 || signal.BitLength is <= 0 or > 64) return [];
        var positions = new int[signal.BitLength];
        // Use a wide intermediate so malformed DBC start positions near
        // int.MaxValue cannot wrap into small valid-looking payload bits.
        long current = signal.StartBit;
        for (var index = 0; index < positions.Length; index++)
        {
            if (current < 0 || current > int.MaxValue)
                return [];
            positions[index] = (int)current;
            current = signal.ByteOrder == DbcByteOrder.Intel
                ? current + 1
                : current % 8 == 0 ? current + 15 : current - 1;
        }
        return positions;
    }
}
