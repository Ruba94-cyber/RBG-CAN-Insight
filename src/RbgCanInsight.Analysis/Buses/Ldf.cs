using System.Globalization;
using System.Text;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Buses;

public sealed record LdfSignal(string Name, int SizeBits, long Init, string Publisher);
public sealed record LdfFrameSignal(string Signal, int OffsetBits);
public sealed record LdfFrame(string Name, int Id, string Publisher, int LengthBytes, IReadOnlyList<LdfFrameSignal> Signals);

/// <summary>One entry of a Signal_encoding_types block.</summary>
public sealed record LdfEncodingValue(
    string Kind, long Min, long Max, double Scale, double Offset, string Unit, string Text);

public sealed record LdfEncoding(string Name, IReadOnlyList<LdfEncodingValue> Values);

/// <summary>Parsed LIN Description File (subset: protocol/speed, nodes, signals, frames, encodings, representation).
/// Sections that are not modelled (schedule tables, diagnostic frames, event-triggered frames, ...) are skipped.</summary>
public sealed class LdfFile
{
    public string ProtocolVersion { get; init; } = "";
    public double? SpeedKbps { get; init; }
    public string MasterNode { get; init; } = "";
    public IReadOnlyList<string> SlaveNodes { get; init; } = [];
    public IReadOnlyDictionary<string, LdfSignal> Signals { get; init; } = new Dictionary<string, LdfSignal>();
    public IReadOnlyList<LdfFrame> Frames { get; init; } = [];
    public IReadOnlyDictionary<string, LdfEncoding> Encodings { get; init; } = new Dictionary<string, LdfEncoding>();
    /// <summary>Signal name to encoding name.</summary>
    public IReadOnlyDictionary<string, string> Representation { get; init; } = new Dictionary<string, string>();
}

public static class LdfParser
{
    private enum T { Word, Str, Sym, End }
    private readonly record struct Tok(T Kind, string Text, int Line);

    /// <summary>Parses LDF text. Throws <see cref="FormatException"/> with a line number on structural errors.</summary>
    public static LdfFile Parse(string text)
    {
        var toks = Tokenize(text);
        var p = 0;
        string? version = null;
        double? speed = null;
        var master = ""; var slaves = new List<string>();
        var signals = new Dictionary<string, LdfSignal>(StringComparer.Ordinal);
        var frames = new List<LdfFrame>();
        var encodings = new Dictionary<string, LdfEncoding>(StringComparer.Ordinal);
        var repr = new Dictionary<string, string>(StringComparer.Ordinal);

        Tok Peek() => toks[p];
        Tok Next() => toks[p].Kind == T.End ? toks[p] : toks[p++];
        void Skip() { if (Next().Kind == T.End) throw Err("unexpected end of file"); }
        FormatException Err(string m) => new($"LDF line {toks[Math.Min(p, toks.Count - 1)].Line}: {m}");
        void Sym(string s) { if (Next() is not { Kind: T.Sym } t || t.Text != s) { throw Err($"expected '{s}'"); } }
        bool TrySym(string s) { if (Peek() is { Kind: T.Sym } t && t.Text == s) { p++; return true; } return false; }
        string Word() { var t = Next(); if (t.Kind is T.End or T.Sym) { throw Err("unexpected token"); } return t.Text; }
        double Num()
        {
            var w = Word();
            // a unit word such as "kbps" or "ms" may follow a number
            if (Peek() is { Kind: T.Word } u && IsUnit(u.Text)) Skip();
            if (w.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(w.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return h;
            return double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
                ? d : throw Err($"'{w}' is not a number");
        }
        void SkipBlock()
        {
            Sym("{");
            for (var depth = 1; depth > 0;)
            {
                var t = Next();
                if (t.Kind == T.End) { throw Err("unbalanced braces"); }
                if (t.Kind == T.Sym) depth += t.Text == "{" ? 1 : t.Text == "}" ? -1 : 0;
            }
        }

        while (Peek().Kind != T.End)
        {
            var name = Word();
            if (TrySym(";")) continue; // e.g. the leading "LIN_description_file;"
            if (TrySym("="))
            {
                var value = new StringBuilder();
                while (!TrySym(";"))
                {
                    if (Peek().Kind == T.End) throw Err("missing ';'");
                    value.Append(Next().Text).Append(' ');
                }
                var v = value.ToString().Trim();
                if (name == "LIN_protocol_version") version = v;
                else if (name == "LIN_speed")
                {
                    var first = v.Split(' ')[0];
                    if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s))
                        speed = v.Contains("bps", StringComparison.OrdinalIgnoreCase) && !v.Contains("kbps", StringComparison.OrdinalIgnoreCase) ? s / 1000 : s;
                }
                continue;
            }
            switch (name)
            {
                case "Nodes":
                    Sym("{");
                    while (!TrySym("}"))
                    {
                        var key = Word(); Sym(":");
                        if (key == "Master")
                        {
                            master = Word();
                            while (TrySym(",")) Num();
                        }
                        else if (key == "Slaves")
                        {
                            slaves.Add(Word());
                            while (TrySym(",")) slaves.Add(Word());
                        }
                        else while (Peek() is { Kind: not T.End } t && !(t.Kind == T.Sym && t.Text == ";")) Skip();
                        Sym(";");
                    }
                    break;
                case "Signals":
                    Sym("{");
                    while (!TrySym("}"))
                    {
                        var sname = Word(); Sym(":");
                        var size = (int)Num(); Sym(",");
                        long init = 0;
                        if (TrySym("{")) { while (!TrySym("}")) Skip(); }
                        else init = (long)Num();
                        Sym(",");
                        var pub = Word();
                        while (TrySym(",")) Word();
                        Sym(";");
                        signals[sname] = new LdfSignal(sname, size, init, pub);
                    }
                    break;
                case "Frames":
                    Sym("{");
                    while (!TrySym("}"))
                    {
                        var fname = Word(); Sym(":");
                        var id = (int)Num(); Sym(",");
                        var pub = Word(); Sym(",");
                        var len = (int)Num();
                        Sym("{");
                        var list = new List<LdfFrameSignal>();
                        while (!TrySym("}"))
                        {
                            var sig = Word(); Sym(",");
                            list.Add(new LdfFrameSignal(sig, (int)Num()));
                            Sym(";");
                        }
                        frames.Add(new LdfFrame(fname, id, pub, len, list));
                    }
                    break;
                case "Signal_encoding_types":
                    Sym("{");
                    while (!TrySym("}"))
                    {
                        var ename = Word(); Sym("{");
                        var values = new List<LdfEncodingValue>();
                        while (!TrySym("}"))
                        {
                            var kind = Word();
                            var args = new List<string>();
                            while (TrySym(",")) args.Add(Next().Text);
                            values.Add(ParseEncodingEntry(kind, args, Peek().Line));
                            Sym(";");
                        }
                        encodings[ename] = new LdfEncoding(ename, values);
                    }
                    break;
                case "Signal_representation":
                    Sym("{");
                    while (!TrySym("}"))
                    {
                        var ename = Word(); Sym(":");
                        repr[Word()] = ename;
                        while (TrySym(",")) repr[Word()] = ename;
                        Sym(";");
                    }
                    break;
                default:
                    SkipBlock();
                    break;
            }
        }
        return new LdfFile
        {
            ProtocolVersion = version?.Trim('"') ?? "", SpeedKbps = speed, MasterNode = master, SlaveNodes = slaves,
            Signals = signals, Frames = frames, Encodings = encodings, Representation = repr
        };
    }

    private static bool IsUnit(string w) => w is "kbps" or "bps" or "ms" or "s" or "us";

    private static LdfEncodingValue ParseEncodingEntry(string kind, List<string> a, int line)
    {
        double D(int i) => i < a.Count && TryNum(a[i], out var v) ? v : throw new FormatException($"LDF line {line}: bad number in {kind}");
        string S(int i) => i < a.Count ? a[i] : "";
        switch (kind)
        {
            case "logical_value":
                return new LdfEncodingValue(kind, (long)D(0), (long)D(0), 1, 0, "", S(1));
            case "physical_value":
                return new LdfEncodingValue(kind, (long)D(0), (long)D(1), D(2), D(3), S(4), "");
            default: // bcd_value, ascii_value
                return new LdfEncodingValue(kind, 0, 0, 1, 0, "", "");
        }
    }

    private static bool TryNum(string w, out double v)
    {
        if (w.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(w.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) { v = h; return true; }
        return double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
    }

    private static List<Tok> Tokenize(string s)
    {
        var list = new List<Tok>();
        var line = 1;
        for (var i = 0; i < s.Length;)
        {
            var c = s[i];
            if (c == '\n') { line++; i++; }
            else if (char.IsWhiteSpace(c)) i++;
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new FormatException($"LDF line {line}: unterminated comment");
                foreach (var ch in s.AsSpan(i, end - i)) if (ch == '\n') line++;
                i = end + 2;
            }
            else if (c == '"')
            {
                var end = s.IndexOf('"', i + 1);
                if (end < 0) throw new FormatException($"LDF line {line}: unterminated string");
                list.Add(new Tok(T.Str, s.Substring(i + 1, end - i - 1), line));
                foreach (var ch in s.AsSpan(i, end - i)) if (ch == '\n') line++;
                i = end + 1;
            }
            else if (c is '{' or '}' or ';' or ':' or ',' or '=') { list.Add(new Tok(T.Sym, c.ToString(), line)); i++; }
            else if (char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '+')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '-' or '.' or '+')) i++;
                list.Add(new Tok(T.Word, s[start..i], line));
            }
            else throw new FormatException($"LDF line {line}: unexpected character '{c}'");
        }
        list.Add(new Tok(T.End, "", line));
        return list;
    }
}

/// <summary>Per frame-signal statistics decoded from recorded LIN data using an LDF.</summary>
public sealed record LdfSignalSummary(
    string Frame, string Signal, int Count, double Min, double Max, long LastRaw, string LastValue, string Unit,
    int FirstIndex, int LastIndex);

public sealed record LdfDecodeResult(
    IReadOnlyList<LdfSignalSummary> Signals, int FramesDecoded, int FramesNotInLdf, int FramesLengthMismatch,
    int FramesSkippedForErrors);

public static class LdfSignalDecoder
{
    public static LdfDecodeResult Decode(LdfFile ldf, IReadOnlyList<LinFrame> frames)
    {
        var byId = ldf.Frames.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var checks = LinFrameAnalyzer.Check(frames);
        var acc = new Dictionary<(string, string), Acc>();
        int decoded = 0, unknown = 0, mismatch = 0, bad = 0;
        foreach (var check in checks)
        {
            var f = check.Frame;
            if (f.Event != LinEventKind.Message || LinProtocol.IsDiagnosticId(f.FrameId)) continue;
            if (check.Pid == LinPidStatus.Invalid || check.Checksum is LinChecksumStatus.Mismatch or LinChecksumStatus.Truncated) { bad++; continue; }
            if (!byId.TryGetValue(f.FrameId, out var def)) { unknown++; continue; }
            if (f.Data.Length != def.LengthBytes) { mismatch++; continue; }
            decoded++;
            foreach (var fs in def.Signals)
            {
                if (!ldf.Signals.TryGetValue(fs.Signal, out var sig)) continue;
                if (sig.SizeBits is < 1 or > 64 || fs.OffsetBits < 0 || fs.OffsetBits + sig.SizeBits > f.Data.Length * 8) continue;
                var raw = ExtractLe(f.Data, fs.OffsetBits, sig.SizeBits);
                var (num, text, unit) = Interpret(ldf, sig, raw);
                var key = (def.Name, sig.Name);
                if (!acc.TryGetValue(key, out var a)) acc[key] = a = new Acc { First = check.Index };
                a.Count++;
                a.Min = Math.Min(a.Min, num); a.Max = Math.Max(a.Max, num);
                a.LastRaw = (long)raw; a.Text = text; a.Unit = unit; a.Last = check.Index;
            }
        }
        var rows = acc.OrderBy(p => p.Key.Item1, StringComparer.Ordinal).ThenBy(p => p.Key.Item2, StringComparer.Ordinal)
            .Select(p => new LdfSignalSummary(p.Key.Item1, p.Key.Item2, p.Value.Count, p.Value.Min, p.Value.Max,
                p.Value.LastRaw, p.Value.Text, p.Value.Unit, p.Value.First, p.Value.Last)).ToArray();
        return new LdfDecodeResult(rows, decoded, unknown, mismatch, bad);
    }

    private sealed class Acc
    {
        public int Count, First, Last; public double Min = double.MaxValue, Max = double.MinValue;
        public long LastRaw; public string Text = "", Unit = "";
    }

    /// <summary>LSB-first bit extraction (LIN signals are little-endian bit fields).</summary>
    public static ulong ExtractLe(byte[] data, int offsetBits, int sizeBits)
    {
        ulong value = 0;
        for (var i = 0; i < sizeBits; i++)
        {
            var bit = offsetBits + i;
            if (((data[bit >> 3] >> (bit & 7)) & 1) != 0) value |= 1UL << i;
        }
        return value;
    }

    private static (double Numeric, string Text, string Unit) Interpret(LdfFile ldf, LdfSignal sig, ulong raw)
    {
        var rawText = raw.ToString(CultureInfo.InvariantCulture);
        if (!ldf.Representation.TryGetValue(sig.Name, out var encName) || !ldf.Encodings.TryGetValue(encName, out var enc))
            return (raw, rawText, "");
        foreach (var v in enc.Values)
            if (v.Kind == "logical_value" && (ulong)v.Min == raw)
                return (raw, $"{v.Text} (raw {raw})", "");
        foreach (var v in enc.Values)
            if (v.Kind == "physical_value" && raw >= (ulong)Math.Max(0, v.Min) && raw <= (ulong)Math.Max(0, v.Max))
            {
                var phys = raw * v.Scale + v.Offset;
                return (phys, phys.ToString("0.######", CultureInfo.InvariantCulture), v.Unit);
            }
        return (raw, rawText + " (no encoding entry matches)", "");
    }
}
