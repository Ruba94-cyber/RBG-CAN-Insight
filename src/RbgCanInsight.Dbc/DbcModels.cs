namespace RbgCanInsight.Dbc;

public enum DbcByteOrder
{
    Motorola,
    Intel
}

public enum DbcSignalValueType
{
    Integer,
    Float32,
    Float64
}

/// <summary>Physical CAN arbitration identity: numeric ID alone is not unique.</summary>
public readonly record struct DbcMessageIdentity(uint Identifier, bool IsExtended);

/// <summary>One extended-multiplexing range (inclusive).</summary>
public readonly record struct DbcMuxRange(long Minimum, long Maximum)
{
    public bool Contains(ulong value) => Minimum >= 0 && Maximum >= Minimum &&
        value >= (ulong)Minimum && value <= (ulong)Maximum;
}

/// <summary>A controller signal and its accepted values from DBC SG_MUL_VAL_.</summary>
public sealed record DbcMultiplexCondition(string Multiplexor, IReadOnlyList<DbcMuxRange> Ranges);

/// <summary>A source-declared raw interval with its own affine scaling.
/// Piecewise conversions must never be reduced to the first segment.</summary>
public sealed record DbcScaleSegment(
    double? RawLower, double? RawUpper, double Factor, double Offset,
    bool LowerInclusive = true, bool UpperInclusive = true)
{
    public bool Contains(double raw) => double.IsFinite(raw) &&
        (RawLower is null || (LowerInclusive ? raw >= RawLower : raw > RawLower)) &&
        (RawUpper is null || (UpperInclusive ? raw <= RawUpper : raw < RawUpper));
}

/// <summary>Original DBC attribute value retained without guessing its vendor
/// type/units or default. RawValue includes original DBC string quotes if present.</summary>
public sealed record DbcSourceAttribute(string Name, string RawValue);

public sealed record DbcDatabase(
    string Name,
    IReadOnlyDictionary<uint, DbcMessage> Messages,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyDictionary<DbcMessageIdentity, DbcMessage>? MessageIdentities = null,
    IReadOnlyList<string>? AttributeDeclarations = null)
{
    /// <summary>Complete frame-format-aware definitions. The old numeric Messages
    /// index remains for legacy callers and cannot represent a numeric collision.</summary>
    public IEnumerable<DbcMessage> AllMessageDefinitions =>
        MessageIdentities is not null ? MessageIdentities.Values : Messages.Values;

    public int MessageDefinitionCount => MessageIdentities?.Count ?? Messages.Count;

    public bool TryGetMessage(uint identifier, bool extended, out DbcMessage message)
    {
        if (MessageIdentities is not null)
            return MessageIdentities.TryGetValue(new(identifier, extended), out message!);
        if (Messages.TryGetValue(identifier, out var candidate) &&
            candidate.IsExtended == extended)
        {
            message = candidate;
            return true;
        }
        message = null!;
        return false;
    }

    public IEnumerable<DbcSignal> AllSignals =>
        AllMessageDefinitions.SelectMany(message => message.Signals);

    public IReadOnlyList<string> ParseWarnings => Warnings ?? [];
    public IReadOnlyList<string> RawAttributeDeclarations => AttributeDeclarations ?? [];
}

/// <summary>Mechanical 29-bit J1939 identifier-field breakdown. The same
/// 29-bit bits may be used for non-J1939 traffic, so this is an interpretation,
/// not proof that a frame belongs to J1939.</summary>
public sealed record J1939IdentifierInfo(
    uint Identifier,
    int Priority,
    bool ExtendedDataPage,
    bool DataPage,
    byte PduFormat,
    byte PduSpecific,
    byte SourceAddress,
    uint Pgn)
{
    public bool IsPdu1 => PduFormat < 240;
    public byte? DestinationAddress => IsPdu1 ? PduSpecific : null;
    public bool IsGlobalDestination => DestinationAddress == 0xFF;
    public string FormatDescription => IsPdu1 ? "PDU1 (destination specific)"
        : "PDU2 (group extension)";
    public string Summary =>
        $"J1939 interpretation · priority {Priority} · PGN 0x{Pgn:X5} · SA 0x{SourceAddress:X2} · " +
        (IsPdu1
            ? $"DA 0x{PduSpecific:X2}" + (IsGlobalDestination ? " (global)" : "")
            : $"group extension 0x{PduSpecific:X2}") +
        $" · {FormatDescription}";
}

/// <summary>SAE J1939 identifier helpers (29-bit PDU: priority, EDP/DP, PF, PS, SA).</summary>
public static class J1939
{
    /// <summary>
    /// Extracts the Parameter Group Number from a 29-bit identifier. For PDU1 (PF &lt; 240) the
    /// PS byte is a destination address and is excluded; for PDU2 it is the group extension.
    /// The source address byte is always excluded, which is what makes PGN matching work when
    /// the same message is sent by different source addresses.
    /// </summary>
    /// <summary>Interpret all 29-bit J1939 fields; requires a valid
    /// 29-bit arbitration identifier.</summary>
    public static J1939IdentifierInfo Explain(uint identifier)
    {
        if (identifier > 0x1FFF_FFFFu)
            throw new ArgumentOutOfRangeException(nameof(identifier));
        return new J1939IdentifierInfo(
            identifier,
            (int)((identifier >> 26) & 0x07),
            ((identifier >> 25) & 0x01) != 0,
            ((identifier >> 24) & 0x01) != 0,
            (byte)((identifier >> 16) & 0xFF),
            (byte)((identifier >> 8) & 0xFF),
            (byte)(identifier & 0xFF),
            ExtractPgn(identifier));
    }

    public static uint ExtractPgn(uint identifier)
    {
        var pf = (identifier >> 16) & 0xFF;
        var raw = (identifier >> 8) & 0x3FFFF; // EDP + DP + PF + PS
        return pf >= 240 ? raw : raw & 0x3FF00;
    }
}

public sealed record DbcMessage(
    uint Identifier,
    string Name,
    int Length,
    string Transmitter,
    bool IsExtended,
    IReadOnlyList<DbcSignal> Signals,
    string Comment = "",
    int? NominalCycleTimeMilliseconds = null,
    IReadOnlyList<DbcSourceAttribute>? Attributes = null)
{
    public IReadOnlyList<DbcSourceAttribute> SourceAttributes => Attributes ?? [];
}

public sealed record DbcSignal(
    string Name,
    int StartBit,
    int BitLength,
    DbcByteOrder ByteOrder,
    bool IsSigned,
    double Factor,
    double Offset,
    double Minimum,
    double Maximum,
    string Unit,
    string Receiver,
    bool IsMultiplexor = false,
    int? MultiplexValue = null,
    IReadOnlyDictionary<long, string>? ValueDescriptions = null,
    string Comment = "",
    DbcSignalValueType ValueType = DbcSignalValueType.Integer,
    IReadOnlyList<DbcMultiplexCondition>? MultiplexConditions = null,
    IReadOnlyList<DbcSourceAttribute>? Attributes = null,
    IReadOnlyList<DbcScaleSegment>? ScaleSegments = null,
    bool PhysicalConversionUnsupported = false)
{
    public IReadOnlyList<DbcSourceAttribute> SourceAttributes => Attributes ?? [];
    public bool UsesRangeDependentScale => ScaleSegments is { Count: > 0 };

    /// <summary>True only when a declared, non-empty [min|max] range exists and the value lies
    /// outside it. "[0|0]" (min == max) is the DBC convention for "no range declared", and a
    /// value sitting exactly on a limit must not be flagged because raw * factor is not exact in
    /// binary floating point (3 * 0.1 > 0.3).</summary>
    public bool IsOutsideDeclaredRange(double physical)
    {
        if (!double.IsFinite(physical) || !(Maximum > Minimum))
            return false;
        var tolerance = 1e-9 * Math.Max(1d, Maximum - Minimum);
        return physical < Minimum - tolerance || physical > Maximum + tolerance;
    }

    /// <summary>Convert only when the raw value belongs to exactly one
    /// coherent source-defined interval. Gaps and conflicting overlaps are
    /// raw-only, not guessed from the first scale.</summary>
    public bool TryGetPhysicalValue(double raw, out double physical)
    {
        physical = double.NaN;
        if (!double.IsFinite(raw) || PhysicalConversionUnsupported)
            return false;

        if (ScaleSegments is { Count: > 0 } segments)
        {
            DbcScaleSegment? selected = null;
            foreach (var segment in segments)
            {
                if (!segment.Contains(raw)) continue;
                if (selected is not null &&
                    (selected.Factor != segment.Factor || selected.Offset != segment.Offset))
                    return false;
                selected = segment;
            }
            if (selected is null) return false;
            physical = raw * selected.Factor + selected.Offset;
        }
        else
        {
            physical = raw * Factor + Offset;
        }
        return double.IsFinite(physical);
    }

    public string? GetValueDescription(double rawValue)
    {
        // IEEE-754 double cannot distinguish every integer above 2^53-1.
        // A rounded 64-bit raw value must not be mislabeled as an adjacent
        // named DBC code. For full-width integers use the long overload.
        const double MaxExactInteger = 9007199254740991d;
        if (ValueDescriptions is null || !double.IsFinite(rawValue) ||
            rawValue != Math.Truncate(rawValue) ||
            rawValue < -MaxExactInteger || rawValue > MaxExactInteger)
            return null;
        return GetValueDescription((long)rawValue);
    }

    /// <summary>Lookup for an exact signed integer code, without round-tripping
    /// through floating point. Necessary for legitimate 64-bit DBC states.</summary>
    public string? GetValueDescription(long rawValue) =>
        ValueDescriptions is not null &&
        ValueDescriptions.TryGetValue(rawValue, out var description)
            ? description : null;
}

public sealed class DbcParseException : Exception
{
    public DbcParseException(string message, int lineNumber)
        : base($"DBC line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
    }

    public int LineNumber { get; }
}
