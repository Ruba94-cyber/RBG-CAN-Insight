using System.Globalization;
using System.Text;

namespace RbgCanInsight.Analysis.Xcp.A2l;

public sealed class A2lFormatException(string message) : Exception(message);

/// <summary>
/// Read-only ASAP2 (A2L) reader. It tokenizes the file into a generic block tree and then extracts the objects this
/// application uses: MEASUREMENT, CHARACTERISTIC, AXIS_PTS, COMPU_METHOD (+COMPU_TAB/VTAB/VTAB_RANGE), RECORD_LAYOUT, MOD_COMMON
/// byte order and the XCP IF_DATA (XCP_ON_CAN identifiers, DAQ_LIST_CAN_ID, EVENT list). Everything else is skipped, never guessed;
/// <c>/include</c> is not followed and A2ML is skipped. Nothing is ever written.
/// </summary>
public static class A2lReader
{
    public const long MaxFileBytes = 512L * 1024 * 1024;
    private const int MaxDepth = 64;

    private readonly record struct Tok(string Text, bool IsString, int Line);

    private sealed class Block
    {
        public required string Name;
        public required int Line;
        public List<Tok> Tokens = [];
        public List<Block> Children = [];
    }

    public static A2lDatabase ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("A2L file not found.", path);
        if (info.Length > MaxFileBytes) throw new A2lFormatException($"A2L file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
        // A2L files are ASCII/Latin-1 in practice; UTF-8 detection is left to StreamReader.
        using var reader = new StreamReader(path, Encoding.Latin1, true);
        return Read(reader.ReadToEnd(), Path.GetFileName(path));
    }

    public static A2lDatabase Read(string text, string sourceName = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        var warnings = new List<string>();
        var root = BuildTree(Tokenize(text, warnings), warnings);

        var project = root.Children.FirstOrDefault(b => b.Name == "PROJECT");
        var module = project?.Children.FirstOrDefault(b => b.Name == "MODULE")
                     ?? root.Children.FirstOrDefault(b => b.Name == "MODULE");
        if (module is null) throw new A2lFormatException("No /begin MODULE block found; this does not look like an A2L file.");
        if (project is not null && project.Children.Count(b => b.Name == "MODULE") > 1)
            warnings.Add("The file has several MODULE blocks; only the first is read.");

        var methods = new Dictionary<string, A2lCompuMethod>(StringComparer.Ordinal);
        var layouts = new Dictionary<string, A2lRecordLayout>(StringComparer.Ordinal);
        var tabs = new Dictionary<string, List<(double, double)>>(StringComparer.Ordinal);
        var vtabs = new Dictionary<string, (List<(double, string)> Pairs, string? Def)>(StringComparer.Ordinal);
        var vranges = new Dictionary<string, (List<(double, double, string)> Rows, string? Def)>(StringComparer.Ordinal);
        bool? bigEndian = null;
        var events = new List<A2lXcpEvent>();
        var can = new List<A2lXcpOnCan>();
        var pending = new List<(string Name, A2lSymbolKind Kind, Block B)>();

        foreach (var b in module.Children)
        {
            switch (b.Name)
            {
                case "MOD_COMMON":
                    for (var i = 0; i < b.Tokens.Count - 1; i++)
                        if (!b.Tokens[i].IsString && b.Tokens[i].Text == "BYTE_ORDER")
                            bigEndian = b.Tokens[i + 1].Text == "MSB_FIRST" ? true : b.Tokens[i + 1].Text == "MSB_LAST" ? false : bigEndian;
                    break;
                case "COMPU_METHOD":
                    if (ParseCompuMethod(b, warnings) is { } cm) methods[cm.Name] = cm;
                    break;
                case "COMPU_TAB": ParseTab(b, tabs, warnings); break;
                case "COMPU_VTAB": ParseVtab(b, vtabs, warnings); break;
                case "COMPU_VTAB_RANGE": ParseVrange(b, vranges, warnings); break;
                case "RECORD_LAYOUT":
                    if (b.Tokens.Count > 0) layouts[b.Tokens[0].Text] = ParseRecordLayout(b);
                    break;
                case "MEASUREMENT": pending.Add((b.Tokens.Count > 0 ? b.Tokens[0].Text : "", A2lSymbolKind.Measurement, b)); break;
                case "CHARACTERISTIC": pending.Add((b.Tokens.Count > 0 ? b.Tokens[0].Text : "", A2lSymbolKind.Characteristic, b)); break;
                case "AXIS_PTS": pending.Add((b.Tokens.Count > 0 ? b.Tokens[0].Text : "", A2lSymbolKind.AxisPoints, b)); break;
                case "IF_DATA": ParseIfData(b, events, can); break;
            }
        }

        foreach (var (name, (pairs, def)) in vtabs)
            foreach (var m in methods.Values.Where(m => m.TableRef == name))
            {
                m.VerbTable = pairs;
                m.VerbDefault = def;
            }
        foreach (var (name, (rows, def)) in vranges)
            foreach (var m in methods.Values.Where(m => m.TableRef == name))
            {
                m.VerbRanges = rows;
                m.VerbDefault ??= def;
            }
        foreach (var (name, pairs) in tabs)
            foreach (var m in methods.Values.Where(m => m.TableRef == name))
                m.Table = pairs;

        var symbols = new List<A2lSymbol>(pending.Count);
        foreach (var (name, kind, b) in pending)
        {
            var s = kind switch
            {
                A2lSymbolKind.Measurement => ParseMeasurement(b),
                A2lSymbolKind.Characteristic => ParseCharacteristic(b, layouts, warnings),
                _ => ParseAxisPts(b, layouts),
            };
            if (s is null)
            {
                if (name.Length > 0 && kind != A2lSymbolKind.AxisPoints)
                    warnings.Add($"{kind} '{name}' (line {b.Line}) has no ECU address or an unreadable header and is not addressable.");
                continue;
            }
            symbols.Add(s);
        }

        return new A2lDatabase
        {
            SourceName = sourceName,
            ProjectName = project?.Tokens.FirstOrDefault().Text ?? "",
            ModuleName = module.Tokens.FirstOrDefault().Text ?? "",
            BigEndian = bigEndian,
            Symbols = symbols,
            CompuMethods = methods,
            RecordLayouts = layouts,
            XcpEvents = events,
            XcpOnCan = can,
            Warnings = warnings,
        };
    }

    // ---------------- tokenizer / tree ----------------

    private static IEnumerable<Tok> Tokenize(string s, List<string> warnings)
    {
        var line = 1;
        var i = 0;
        var n = s.Length;
        var unterminated = false;
        while (i < n)
        {
            var c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? n : end + 2;
                for (var k = i; k < stop; k++) if (s[k] == '\n') line++;
                i = stop;
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                while (i < n && s[i] != '\n') i++;
                continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder();
                var startLine = line;
                i++;
                var closed = false;
                while (i < n)
                {
                    var d = s[i];
                    if (d == '\\' && i + 1 < n && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    if (d == '"')
                    {
                        if (i + 1 < n && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        closed = true; i++; break;
                    }
                    if (d == '\n') line++;
                    sb.Append(d); i++;
                }
                if (!closed) unterminated = true;
                yield return new Tok(sb.ToString(), true, startLine);
                continue;
            }
            var st = i;
            while (i < n && !char.IsWhiteSpace(s[i]) && s[i] != '"') i++;
            yield return new Tok(s[st..i], false, line);
        }
        if (unterminated) warnings.Add("A string literal is not terminated; the rest of the file may be misread.");
    }

    private static Block BuildTree(IEnumerable<Tok> tokens, List<string> warnings)
    {
        var root = new Block { Name = "<root>", Line = 0 };
        var stack = new Stack<Block>();
        stack.Push(root);
        using var e = tokens.GetEnumerator();
        bool Next(out Tok t) { if (e.MoveNext()) { t = e.Current; return true; } t = default; return false; }
        while (Next(out var t))
        {
            if (t.IsString) { stack.Peek().Tokens.Add(t); continue; }
            if (t.Text == "/begin")
            {
                if (!Next(out var nameTok)) break;
                if (stack.Count > MaxDepth) throw new A2lFormatException($"Blocks are nested deeper than {MaxDepth} levels (line {t.Line}).");
                var b = new Block { Name = nameTok.Text, Line = t.Line };
                stack.Peek().Children.Add(b);
                if (b.Name == "A2ML")
                {
                    // Skip the AML grammar entirely: it is not A2L data.
                    while (Next(out var skip) && !(skip.Text == "/end" && Next(out var endName) && endName.Text == "A2ML")) { }
                    continue;
                }
                stack.Push(b);
            }
            else if (t.Text == "/end")
            {
                if (!Next(out var endName)) break;
                if (stack.Count <= 1) { warnings.Add($"Line {t.Line}: /end {endName.Text} without matching /begin."); continue; }
                var top = stack.Pop();
                if (top.Name != endName.Text)
                    warnings.Add($"Line {t.Line}: /end {endName.Text} closes /begin {top.Name} (opened at line {top.Line}).");
            }
            else if (t.Text == "/include")
            {
                Next(out var inc);
                warnings.Add($"Line {t.Line}: /include \"{inc.Text}\" is not followed; objects in that file are missing.");
            }
            else stack.Peek().Tokens.Add(t);
        }
        if (stack.Count > 1) warnings.Add($"Block '{stack.Peek().Name}' (line {stack.Peek().Line}) is not closed; the file may be truncated.");
        return root;
    }

    // ---------------- helpers ----------------

    private static bool TryNum(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
        {
            var neg = text[0] == '-';
            if (!ulong.TryParse(text.AsSpan(neg ? 3 : 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var u)) return false;
            value = neg ? -(double)u : u;
            return true;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static bool TryU64(string? text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryInt(string? text, out int value)
    {
        value = 0;
        if (!TryNum(text, out var d) || d != Math.Truncate(d) || d < int.MinValue || d > int.MaxValue) return false;
        value = (int)d;
        return true;
    }

    private static string Tx(List<Tok> t, int i) => i < t.Count ? t[i].Text : "";

    /// <summary>Value of the optional keyword <paramref name="key"/> (the token after it), searching from <paramref name="start"/>.</summary>
    private static string? Opt(List<Tok> t, int start, string key)
    {
        for (var i = start; i < t.Count - 1; i++)
            if (!t[i].IsString && t[i].Text == key) return t[i + 1].Text;
        return null;
    }

    // ---------------- COMPU ----------------

    private static A2lCompuMethod? ParseCompuMethod(Block b, List<string> warnings)
    {
        var t = b.Tokens;
        if (t.Count < 5) return null;
        var type = t[2].Text switch
        {
            "IDENTICAL" => A2lConversionType.Identical,
            "LINEAR" => A2lConversionType.Linear,
            "RAT_FUNC" => A2lConversionType.RatFunc,
            "TAB_INTP" => A2lConversionType.TabIntp,
            "TAB_NOINTP" => A2lConversionType.TabNoIntp,
            "TAB_VERB" => A2lConversionType.TabVerb,
            "FORM" => A2lConversionType.Form,
            _ => A2lConversionType.Unknown,
        };
        double[]? Nums(string key, int count)
        {
            for (var i = 5; i < t.Count; i++)
            {
                if (t[i].IsString || t[i].Text != key) continue;
                var r = new double[count];
                for (var k = 0; k < count; k++)
                    if (i + 1 + k >= t.Count || !TryNum(t[i + 1 + k].Text, out r[k])) { warnings.Add($"COMPU_METHOD '{t[0].Text}': malformed {key}."); return null; }
                return r;
            }
            return null;
        }
        return new A2lCompuMethod
        {
            Name = t[0].Text,
            LongIdentifier = t[1].Text,
            Type = type,
            Format = t[3].Text,
            Unit = t[4].Text,
            Coeffs = Nums("COEFFS", 6),
            CoeffsLinear = Nums("COEFFS_LINEAR", 2),
            TableRef = Opt(t, 5, "COMPU_TAB_REF"),
            HasFormula = b.Children.Any(c => c.Name == "FORMULA"),
        };
    }

    private static void ParseTab(Block b, Dictionary<string, List<(double, double)>> tabs, List<string> warnings)
    {
        var t = b.Tokens;
        if (t.Count < 4 || !TryInt(t[3].Text, out var n) || n < 0) return;
        var list = new List<(double, double)>();
        for (var i = 0; i < n; i++)
        {
            if (4 + 2 * i + 1 >= t.Count || !TryNum(t[4 + 2 * i].Text, out var a) || !TryNum(t[5 + 2 * i].Text, out var o))
            { warnings.Add($"COMPU_TAB '{t[0].Text}' is truncated or malformed."); return; }
            list.Add((a, o));
        }
        tabs[t[0].Text] = list;
    }

    private static void ParseVtab(Block b, Dictionary<string, (List<(double, string)>, string?)> tabs, List<string> warnings)
    {
        var t = b.Tokens;
        if (t.Count < 4 || !TryInt(t[3].Text, out var n) || n < 0) return;
        var list = new List<(double, string)>();
        for (var i = 0; i < n; i++)
        {
            if (4 + 2 * i + 1 >= t.Count || !TryNum(t[4 + 2 * i].Text, out var a))
            { warnings.Add($"COMPU_VTAB '{t[0].Text}' is truncated or malformed."); return; }
            list.Add((a, t[5 + 2 * i].Text));
        }
        tabs[t[0].Text] = (list, Opt(t, 4 + 2 * n, "DEFAULT_VALUE"));
    }

    private static void ParseVrange(Block b, Dictionary<string, (List<(double, double, string)>, string?)> tabs, List<string> warnings)
    {
        var t = b.Tokens;
        if (t.Count < 3 || !TryInt(t[2].Text, out var n) || n < 0) return;
        var list = new List<(double, double, string)>();
        for (var i = 0; i < n; i++)
        {
            var o = 3 + 3 * i;
            if (o + 2 >= t.Count || !TryNum(t[o].Text, out var lo) || !TryNum(t[o + 1].Text, out var hi))
            { warnings.Add($"COMPU_VTAB_RANGE '{t[0].Text}' is truncated or malformed."); return; }
            list.Add((lo, hi, t[o + 2].Text));
        }
        tabs[t[0].Text] = (list, Opt(t, 3 + 3 * n, "DEFAULT_VALUE"));
    }

    // ---------------- RECORD_LAYOUT ----------------

    private static readonly HashSet<string> LayoutKeywords = new(StringComparer.Ordinal)
    {
        "FNC_VALUES", "IDENTIFICATION", "STATIC_RECORD_LAYOUT", "RESERVED", "ALIGNMENT_BYTE", "ALIGNMENT_WORD", "ALIGNMENT_LONG",
        "ALIGNMENT_INT64", "ALIGNMENT_FLOAT16_IEEE", "ALIGNMENT_FLOAT32_IEEE", "ALIGNMENT_FLOAT64_IEEE",
        "AXIS_PTS_X", "AXIS_PTS_Y", "AXIS_PTS_Z", "AXIS_PTS_4", "AXIS_PTS_5",
        "AXIS_RESCALE_X", "AXIS_RESCALE_Y", "AXIS_RESCALE_Z", "AXIS_RESCALE_4", "AXIS_RESCALE_5",
        "NO_AXIS_PTS_X", "NO_AXIS_PTS_Y", "NO_AXIS_PTS_Z", "NO_AXIS_PTS_4", "NO_AXIS_PTS_5",
        "NO_RESCALE_X", "NO_RESCALE_Y", "NO_RESCALE_Z", "NO_RESCALE_4", "NO_RESCALE_5",
        "OFFSET_X", "OFFSET_Y", "OFFSET_Z", "OFFSET_4", "OFFSET_5", "DIST_OP_X", "DIST_OP_Y", "DIST_OP_Z", "DIST_OP_4", "DIST_OP_5",
        "FIX_NO_AXIS_PTS_X", "FIX_NO_AXIS_PTS_Y", "FIX_NO_AXIS_PTS_Z", "FIX_NO_AXIS_PTS_4", "FIX_NO_AXIS_PTS_5",
        "SHIFT_OP_X", "SHIFT_OP_Y", "SHIFT_OP_Z", "SHIFT_OP_4", "SHIFT_OP_5", "SRC_ADDR_X", "SRC_ADDR_Y", "SRC_ADDR_Z", "SRC_ADDR_4", "SRC_ADDR_5",
        "RIP_ADDR_W", "RIP_ADDR_X", "RIP_ADDR_Y", "RIP_ADDR_Z", "RIP_ADDR_4", "RIP_ADDR_5",
    };

    private static A2lRecordLayout ParseRecordLayout(Block b)
    {
        var entries = new List<A2lRecordLayoutEntry>();
        string? key = null;
        var ps = new List<string>();
        for (var i = 1; i < b.Tokens.Count; i++)
        {
            var tk = b.Tokens[i];
            if (!tk.IsString && LayoutKeywords.Contains(tk.Text))
            {
                if (key is not null) entries.Add(new A2lRecordLayoutEntry(key, ps));
                key = tk.Text; ps = [];
            }
            else if (key is not null) ps.Add(tk.Text);
        }
        if (key is not null) entries.Add(new A2lRecordLayoutEntry(key, ps));
        return new A2lRecordLayout { Name = b.Tokens[0].Text, Entries = entries };
    }

    // ---------------- symbols ----------------

    private static bool? ByteOrder(List<Tok> t, int start) => Opt(t, start, "BYTE_ORDER") switch
    {
        "MSB_FIRST" => true,
        "MSB_LAST" => false,
        _ => null,
    };

    private static (int Count, string? Note) MatrixCount(List<Tok> t, int start)
    {
        for (var i = start; i < t.Count; i++)
        {
            if (t[i].IsString) continue;
            if (t[i].Text == "MATRIX_DIM")
            {
                var total = 1L;
                for (var k = 1; k <= 3 && i + k < t.Count && TryInt(t[i + k].Text, out var d) && d > 0; k++) total *= d;
                return (total > 0 && total < 1_000_000 ? (int)total : 1, null);
            }
            if (t[i].Text == "ARRAY_SIZE" && TryInt(Tx(t, i + 1), out var n) && n > 0) return (n, null);
        }
        return (1, null);
    }

    private static A2lSymbol? ParseMeasurement(Block b)
    {
        var t = b.Tokens;
        if (t.Count < 8) return null;
        var addr = Opt(t, 8, "ECU_ADDRESS");
        if (!TryU64(addr, out var address)) return null;
        var type = A2lDataTypes.Parse(t[2].Text);
        var (count, _) = MatrixCount(t, 8);
        var ext = TryInt(Opt(t, 8, "ECU_ADDRESS_EXTENSION"), out var e) ? e : 0;
        ulong? mask = TryU64(Opt(t, 8, "BIT_MASK"), out var m) ? m : null;
        var events = new List<int>();
        foreach (var c in b.Children.Where(c => c.Name == "IF_DATA"))
            CollectFixedEvents(c, events);
        return new A2lSymbol
        {
            Name = t[0].Text, Kind = A2lSymbolKind.Measurement, LongIdentifier = t[1].Text, Address = address, Extension = ext,
            DataType = type, ElementCount = count, SizeBytes = A2lDataTypes.SizeOf(type) * count,
            ConversionName = t[3].Text, Unit = Opt(t, 8, "PHYS_UNIT") ?? "", BigEndian = ByteOrder(t, 8), BitMask = mask,
            FixedEvents = events,
        };
    }

    private static void CollectFixedEvents(Block b, List<int> events)
    {
        if (b.Name == "FIXED_EVENT_LIST")
            for (var i = 0; i < b.Tokens.Count - 1; i++)
                if (b.Tokens[i].Text == "EVENT" && TryInt(b.Tokens[i + 1].Text, out var ev)) events.Add(ev);
        foreach (var c in b.Children) CollectFixedEvents(c, events);
    }

    private static A2lSymbol? ParseCharacteristic(Block b, Dictionary<string, A2lRecordLayout> layouts, List<string> warnings)
    {
        var t = b.Tokens;
        if (t.Count < 9) return null;
        if (!TryU64(t[3].Text, out var address)) return null;
        var kind = t[2].Text;
        layouts.TryGetValue(t[4].Text, out var layout);
        var type = layout?.FunctionValueType ?? A2lDataType.Unknown;
        var (count, _) = MatrixCount(t, 9);
        var size = 0;
        var elementSize = A2lDataTypes.SizeOf(type);
        if (elementSize > 0)
        {
            if (kind == "VALUE") size = elementSize;
            else if (kind == "VAL_BLK")
            {
                var n = TryInt(Opt(t, 9, "NUMBER"), out var num) && num > 0 ? num : count;
                size = elementSize * n;
                count = n;
            }
            else if (kind == "ASCII" && TryInt(Opt(t, 9, "NUMBER"), out var chars) && chars > 0)
            {
                size = chars; count = chars;
            }
        }
        var ext = TryInt(Opt(t, 9, "ECU_ADDRESS_EXTENSION"), out var e) ? e : 0;
        ulong? mask = TryU64(Opt(t, 9, "BIT_MASK"), out var m) ? m : null;
        return new A2lSymbol
        {
            Name = t[0].Text, Kind = A2lSymbolKind.Characteristic, LongIdentifier = t[1].Text, Address = address, Extension = ext,
            DataType = type, ElementCount = count, SizeBytes = size, ConversionName = t[6].Text, Unit = Opt(t, 9, "PHYS_UNIT") ?? "",
            BigEndian = ByteOrder(t, 9), CharacteristicType = kind, RecordLayout = t[4].Text, BitMask = mask,
        };
    }

    private static A2lSymbol? ParseAxisPts(Block b, Dictionary<string, A2lRecordLayout> layouts)
    {
        var t = b.Tokens;
        if (t.Count < 10 || !TryU64(t[2].Text, out var address)) return null;
        layouts.TryGetValue(t[4].Text, out var layout);
        var ext = TryInt(Opt(t, 10, "ECU_ADDRESS_EXTENSION"), out var e) ? e : 0;
        return new A2lSymbol
        {
            Name = t[0].Text, Kind = A2lSymbolKind.AxisPoints, LongIdentifier = t[1].Text, Address = address, Extension = ext,
            DataType = layout?.FunctionValueType ?? A2lDataType.Unknown, SizeBytes = 0, ConversionName = t[6].Text,
            Unit = Opt(t, 10, "PHYS_UNIT") ?? "", RecordLayout = t[4].Text,
        };
    }

    // ---------------- IF_DATA (XCP) ----------------

    private static void ParseIfData(Block ifData, List<A2lXcpEvent> events, List<A2lXcpOnCan> can)
    {
        var tag = ifData.Tokens.FirstOrDefault().Text ?? "";
        void Walk(Block b)
        {
            if (b.Name == "EVENT" && b.Tokens.Count >= 4 && b.Tokens[0].IsString && TryInt(b.Tokens[2].Text, out var ch))
            {
                int? Opti(int idx) => TryInt(Tx(b.Tokens, idx), out var v) ? v : null;
                events.Add(new A2lXcpEvent(b.Tokens[0].Text, b.Tokens[1].Text, ch, Tx(b.Tokens, 3), Opti(4), Opti(5), Opti(6), Opti(7)));
            }
            else if (b.Name == "XCP_ON_CAN")
                can.Add(ParseXcpOnCan(b, tag));
            foreach (var c in b.Children) Walk(c);
        }
        // Skip the tag token when walking: it is only used as a label.
        Walk(ifData);
    }

    private static A2lXcpOnCan ParseXcpOnCan(Block b, string tag)
    {
        (uint? Id, bool Ext) Id(string key)
        {
            var text = Opt(b.Tokens, 0, key);
            if (text is null || !TryU64(text, out var v) || v > uint.MaxValue) return (null, false);
            return ((uint)(v & 0x7FFF_FFFFu), (v & 0x8000_0000u) != 0);
        }
        var master = Id("CAN_ID_MASTER");
        var slave = Id("CAN_ID_SLAVE");
        var bcast = Id("CAN_ID_BROADCAST");
        var lists = new List<(int, uint, bool)>();
        foreach (var d in b.Children.Where(c => c.Name == "DAQ_LIST_CAN_ID"))
            if (TryInt(Tx(d.Tokens, 0), out var list) && Opt(d.Tokens, 1, "FIXED") is { } fixedId && TryU64(fixedId, out var v) && v <= uint.MaxValue)
                lists.Add((list, (uint)(v & 0x7FFF_FFFFu), (v & 0x8000_0000u) != 0));
        return new A2lXcpOnCan(master.Id, master.Ext, slave.Id, slave.Ext, bcast.Id, bcast.Ext,
            TryInt(Opt(b.Tokens, 0, "BAUDRATE"), out var baud) ? baud : null,
            b.Children.Any(c => c.Name == "CAN_FD") || b.Tokens.Any(x => !x.IsString && x.Text == "CAN_FD") ? true : null,
            lists, tag);
    }
}
