using System.Globalization;

namespace RbgCanInsight.Analysis.Xcp.A2l;

/// <summary>A2L (ASAM MCD-2 MC) scalar data types.</summary>
public enum A2lDataType { Unknown, UByte, SByte, UWord, SWord, ULong, SLong, UInt64, Int64, Float16, Float32, Float64 }

public static class A2lDataTypes
{
    public static A2lDataType Parse(string? text) => text switch
    {
        "UBYTE" => A2lDataType.UByte,
        "SBYTE" => A2lDataType.SByte,
        "UWORD" => A2lDataType.UWord,
        "SWORD" => A2lDataType.SWord,
        "ULONG" => A2lDataType.ULong,
        "SLONG" => A2lDataType.SLong,
        "A_UINT64" => A2lDataType.UInt64,
        "A_INT64" => A2lDataType.Int64,
        "FLOAT16_IEEE" => A2lDataType.Float16,
        "FLOAT32_IEEE" => A2lDataType.Float32,
        "FLOAT64_IEEE" => A2lDataType.Float64,
        _ => A2lDataType.Unknown,
    };

    public static int SizeOf(A2lDataType t) => t switch
    {
        A2lDataType.UByte or A2lDataType.SByte => 1,
        A2lDataType.UWord or A2lDataType.SWord or A2lDataType.Float16 => 2,
        A2lDataType.ULong or A2lDataType.SLong or A2lDataType.Float32 => 4,
        A2lDataType.UInt64 or A2lDataType.Int64 or A2lDataType.Float64 => 8,
        _ => 0,
    };

    /// <summary>Decodes <paramref name="bytes"/> (exactly the type's size) to a number; <c>null</c> when unsupported.</summary>
    public static double? Decode(A2lDataType t, ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        var n = SizeOf(t);
        if (n == 0 || bytes.Length != n) return null;
        ulong u = 0;
        for (var i = 0; i < n; i++)
            u = bigEndian ? (u << 8) | bytes[i] : u | ((ulong)bytes[i] << (8 * i));
        return t switch
        {
            A2lDataType.UByte or A2lDataType.UWord or A2lDataType.ULong or A2lDataType.UInt64 => u,
            A2lDataType.SByte => (sbyte)u,
            A2lDataType.SWord => (short)u,
            A2lDataType.SLong => (int)u,
            A2lDataType.Int64 => (long)u,
            A2lDataType.Float16 => (double)BitConverter.UInt16BitsToHalf((ushort)u),
            A2lDataType.Float32 => BitConverter.UInt32BitsToSingle((uint)u),
            A2lDataType.Float64 => BitConverter.UInt64BitsToDouble(u),
            _ => null,
        };
    }
}

public enum A2lConversionType { Identical, Linear, RatFunc, TabIntp, TabNoIntp, TabVerb, Form, Unknown }

/// <summary>COMPU_METHOD with the referenced tables resolved. Only conversions that are exactly invertible are evaluated.</summary>
public sealed class A2lCompuMethod
{
    public required string Name { get; init; }
    public string LongIdentifier { get; init; } = "";
    public A2lConversionType Type { get; init; }
    public string Format { get; init; } = "";
    public string Unit { get; init; } = "";
    public double[]? Coeffs { get; init; }
    public double[]? CoeffsLinear { get; init; }
    public string? TableRef { get; init; }
    public IReadOnlyList<(double In, double Out)> Table { get; internal set; } = [];
    public IReadOnlyList<(double In, string Out)> VerbTable { get; internal set; } = [];
    public IReadOnlyList<(double Low, double High, string Out)> VerbRanges { get; internal set; } = [];
    public string? VerbDefault { get; internal set; }
    public bool HasFormula { get; init; }

    /// <summary>Converts an internal (raw) value to its physical text. <paramref name="note"/> explains refusals.</summary>
    public bool TryConvert(double raw, out double physical, out string? text, out string? note)
    {
        physical = double.NaN; text = null; note = null;
        switch (Type)
        {
            case A2lConversionType.Identical:
                physical = raw; return true;
            case A2lConversionType.Linear when CoeffsLinear is { Length: 2 } l:
                physical = l[0] * raw + l[1]; return true;
            case A2lConversionType.RatFunc when Coeffs is { Length: 6 } c:
                // ASAP2: INT = (a*PHYS^2 + b*PHYS + c) / (d*PHYS^2 + e*PHYS + f). Only a = d = 0 is inverted exactly.
                if (c[0] == 0 && c[3] == 0)
                {
                    var den = c[1] - c[4] * raw;
                    if (den == 0) { note = "RAT_FUNC is singular for this value"; return false; }
                    physical = (c[5] * raw - c[2]) / den; return true;
                }
                note = $"RAT_FUNC '{Name}' has quadratic terms; raw value shown";
                return false;
            case A2lConversionType.TabIntp when Table.Count > 0:
                physical = Interpolate(raw); return !double.IsNaN(physical);
            case A2lConversionType.TabNoIntp when Table.Count > 0:
                foreach (var (i, o) in Table) if (i == raw) { physical = o; return true; }
                note = $"value {raw.ToString(CultureInfo.InvariantCulture)} is not in COMPU_TAB '{TableRef}'";
                return false;
            case A2lConversionType.TabVerb:
                foreach (var (i, o) in VerbTable) if (i == raw) { text = o; return true; }
                foreach (var (lo, hi, o) in VerbRanges) if (raw >= lo && raw <= hi) { text = o; return true; }
                if (VerbDefault is not null) { text = VerbDefault; return true; }
                note = $"value {raw.ToString(CultureInfo.InvariantCulture)} has no text in '{TableRef}'";
                return false;
            case A2lConversionType.Form:
                note = $"FORMULA conversion '{Name}' is not evaluated; raw value shown";
                return false;
            default:
                note = $"conversion '{Name}' ({Type}) is incomplete or unsupported; raw value shown";
                return false;
        }
    }

    private double Interpolate(double x)
    {
        // TAB_INTP maps internal -> physical by linear interpolation; values outside the table are not extrapolated.
        var t = Table;
        if (x < t.Min(p => p.In) || x > t.Max(p => p.In)) return double.NaN;
        (double In, double Out)? lo = null, hi = null;
        foreach (var p in t)
        {
            if (p.In <= x && (lo is null || p.In > lo.Value.In)) lo = p;
            if (p.In >= x && (hi is null || p.In < hi.Value.In)) hi = p;
        }
        if (lo is null || hi is null) return double.NaN;
        if (hi.Value.In == lo.Value.In) return lo.Value.Out;
        return lo.Value.Out + (x - lo.Value.In) * (hi.Value.Out - lo.Value.Out) / (hi.Value.In - lo.Value.In);
    }
}

public sealed record A2lRecordLayoutEntry(string Keyword, IReadOnlyList<string> Parameters);

public sealed class A2lRecordLayout
{
    public required string Name { get; init; }
    public IReadOnlyList<A2lRecordLayoutEntry> Entries { get; init; } = [];

    /// <summary>Data type of the FNC_VALUES entry, if the layout has one.</summary>
    public A2lDataType FunctionValueType
    {
        get
        {
            var e = Entries.FirstOrDefault(x => x.Keyword == "FNC_VALUES");
            return e is { Parameters.Count: >= 2 } ? A2lDataTypes.Parse(e.Parameters[1]) : A2lDataType.Unknown;
        }
    }
}

/// <summary>Kind of A2L object that occupies ECU memory.</summary>
public enum A2lSymbolKind { Measurement, Characteristic, AxisPoints }

/// <summary>One A2L object with an ECU address (MEASUREMENT, CHARACTERISTIC or AXIS_PTS).</summary>
public sealed class A2lSymbol
{
    public required string Name { get; init; }
    public required A2lSymbolKind Kind { get; init; }
    public string LongIdentifier { get; init; } = "";
    public ulong Address { get; init; }
    public int Extension { get; init; }
    public A2lDataType DataType { get; init; }
    /// <summary>Size in bytes of the whole object; 0 when it cannot be computed reliably (maps, curves, axes).</summary>
    public int SizeBytes { get; init; }
    public int ElementCount { get; init; } = 1;
    public string ConversionName { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool? BigEndian { get; init; }
    public string? CharacteristicType { get; init; }
    public string? RecordLayout { get; init; }
    public ulong? BitMask { get; init; }
    public IReadOnlyList<int> FixedEvents { get; init; } = [];

    public ulong EndExclusive => Address + (ulong)Math.Max(SizeBytes, 1);
}

public sealed record A2lXcpEvent(string Name, string ShortName, int Channel, string Direction, int? MaxDaqLists, int? TimeCycle, int? TimeUnit, int? Priority);

/// <summary>XCP_ON_CAN IF_DATA. Identifiers with bit 31 set are treated as 29-bit identifiers (ASAM XCP on CAN convention).</summary>
public sealed record A2lXcpOnCan(
    uint? MasterId, bool MasterExtended, uint? SlaveId, bool SlaveExtended, uint? BroadcastId, bool BroadcastExtended,
    int? Baudrate, bool? CanFd, IReadOnlyList<(int List, uint Id, bool Extended)> DaqListIds, string Tag)
{
    /// <summary>Master = CRO (command) id, slave = DTO (response) id.</summary>
    public XcpEndpoint? ToEndpoint()
    {
        if (MasterId is not { } m || SlaveId is not { } s) return null;
        var daq = DaqListIds.Select(d => d.Id).Where(id => id != m && id != s).Distinct().ToList();
        return new XcpEndpoint(XcpProtocol.Xcp, m, s, MasterExtended, SlaveExtended, null, daq.Count > 0 ? daq : null,
            DaqListIds.Any(d => d.Extended));
    }
}

/// <summary>Read-only view of an A2L file at the depth this application implements.</summary>
public sealed class A2lDatabase
{
    public string SourceName { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string ModuleName { get; init; } = "";
    public bool? BigEndian { get; init; }
    public IReadOnlyList<A2lSymbol> Symbols { get; init; } = [];
    public IReadOnlyDictionary<string, A2lCompuMethod> CompuMethods { get; init; } = new Dictionary<string, A2lCompuMethod>();
    public IReadOnlyDictionary<string, A2lRecordLayout> RecordLayouts { get; init; } = new Dictionary<string, A2lRecordLayout>();
    public IReadOnlyList<A2lXcpEvent> XcpEvents { get; init; } = [];
    public IReadOnlyList<A2lXcpOnCan> XcpOnCan { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int MeasurementCount => Symbols.Count(s => s.Kind == A2lSymbolKind.Measurement);
    public int CharacteristicCount => Symbols.Count(s => s.Kind == A2lSymbolKind.Characteristic);

    private A2lSymbol[]? _sorted;
    private Dictionary<string, A2lSymbol>? _byName;

    private A2lSymbol[] Sorted => _sorted ??= Symbols.Where(s => s.Address != 0 || s.SizeBytes > 0)
        .OrderBy(s => s.Address).ThenBy(s => s.Extension).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();

    public A2lSymbol? Find(string name) =>
        (_byName ??= Symbols.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal))
            .GetValueOrDefault(name);

    /// <summary>
    /// Finds the object at <paramref name="address"/>. Exact start addresses win. Containment is only used for objects whose size is
    /// known (scalars, arrays/VAL_BLK, ASCII); maps/curves resolve by start address only. <c>null</c> when nothing matches.
    /// </summary>
    public A2lSymbolMatch? Resolve(ulong address, int extension = 0)
    {
        var arr = Sorted;
        var lo = 0; var hi = arr.Length - 1; var idx = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (arr[mid].Address <= address) { idx = mid; lo = mid + 1; } else hi = mid - 1;
        }
        A2lSymbolMatch? best = null;
        for (var i = idx; i >= 0 && idx - i < 64; i--)
        {
            var s = arr[i];
            if (s.Extension != extension) continue;
            if (s.Address == address)
            {
                var m = new A2lSymbolMatch(s, 0, true);
                if (best is null || best.Offset != 0 || Prefer(s, best.Symbol)) best = m;
            }
            else if (s.SizeBytes > 0 && address < s.EndExclusive && best is null)
                best = new A2lSymbolMatch(s, (int)(address - s.Address), false);
        }
        return best;
    }

    private static bool Prefer(A2lSymbol a, A2lSymbol b) => a.Kind == A2lSymbolKind.Measurement && b.Kind != A2lSymbolKind.Measurement;

    /// <summary>Converts raw bytes of <paramref name="symbol"/> (exactly its scalar size) to display text, e.g. <c>1500 rpm</c>.</summary>
    public string? FormatValue(A2lSymbol symbol, ReadOnlySpan<byte> bytes, bool defaultBigEndian, out string? note)
    {
        note = null;
        var type = symbol.DataType;
        var raw = A2lDataTypes.Decode(type, bytes, symbol.BigEndian ?? BigEndian ?? defaultBigEndian);
        if (raw is null) { note = "unsupported data type or size"; return null; }
        var v = raw.Value;
        if (symbol.BitMask is { } mask && type != A2lDataType.Float32 && type != A2lDataType.Float64 && type != A2lDataType.Float16)
        {
            var masked = (ulong)(long)v & mask;
            while (mask != 0 && (mask & 1) == 0) { mask >>= 1; masked >>= 1; }
            v = masked;
        }
        var inv = CultureInfo.InvariantCulture;
        if (CompuMethods.TryGetValue(symbol.ConversionName, out var cm))
        {
            if (cm.TryConvert(v, out var phys, out var text, out var cnote))
            {
                if (text is not null) return text;
                var unit = cm.Unit.Length > 0 ? cm.Unit : symbol.Unit;
                return phys.ToString("0.######", inv) + (unit.Length > 0 ? " " + unit : "");
            }
            note = cnote;
        }
        else if (symbol.ConversionName != "NO_COMPU_METHOD" && symbol.ConversionName.Length > 0)
            note = $"COMPU_METHOD '{symbol.ConversionName}' not found";
        return v.ToString("0.######", inv) + (symbol.Unit.Length > 0 ? " " + symbol.Unit : "") + (note is null ? "" : " (raw)");
    }
}

public sealed record A2lSymbolMatch(A2lSymbol Symbol, int Offset, bool Exact)
{
    public string Text => Offset == 0 ? Symbol.Name : $"{Symbol.Name}+{Offset}";
}
