using System.Collections.Immutable;
using System.Globalization;

namespace RbgCanInsight.Analysis.Derived;

/// <summary>
/// Time-ordered (non-decreasing) samples of one signal: timestamps in nanoseconds, plus values.
/// Semantics are sample-and-hold: the value at time t is the value of the last sample with
/// timestamp &lt;= t (for equal timestamps the later sample wins); before the first sample it is undefined (NaN).
/// </summary>
public sealed class SampleSeries
{
    public SampleSeries(IReadOnlyList<long> timestampsNanoseconds, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(timestampsNanoseconds);
        ArgumentNullException.ThrowIfNull(values);
        if (timestampsNanoseconds.Count != values.Count)
        {
            throw new ArgumentException("Timestamps and values must have the same length.");
        }

        for (var i = 1; i < timestampsNanoseconds.Count; i++)
        {
            if (timestampsNanoseconds[i] < timestampsNanoseconds[i - 1])
            {
                throw new ArgumentException("Timestamps must be non-decreasing.", nameof(timestampsNanoseconds));
            }
        }

        Timestamps = [.. timestampsNanoseconds];
        Values = [.. values];
    }

    public ImmutableArray<long> Timestamps { get; }
    public ImmutableArray<double> Values { get; }
    public int Count => Timestamps.Length;
}

/// <summary>A compile error with the 0-based character position in the source text.</summary>
public sealed record ExpressionError(string Message, int Position);

public sealed class ExpressionSyntaxException : FormatException
{
    public ExpressionSyntaxException(ExpressionError error)
        : base($"{error.Message} (at position {error.Position})")
    {
        Error = error;
    }

    public ExpressionError Error { get; }
    public int Position => Error.Position;
}

/// <summary>
/// Safe expression language for derived signals. Pure hand-written parser + tree interpreter: no
/// dynamic code generation, no reflection, no member access.
///
/// Grammar (lowest to highest precedence): <c>||</c>; <c>&amp;&amp;</c>; <c>== !=</c>; <c>&lt; &lt;= &gt; &gt;=</c>;
/// <c>+ -</c>; <c>* / %</c>; unary <c>- + !</c>; primary (number, variable, <c>( )</c>, call).
/// Variables are identifiers <c>[A-Za-z_][A-Za-z0-9_]*</c> or any text in backticks (`Msg.Signal`).
/// A dot outside backticks is a syntax error. Booleans are numbers: comparison/logic yield 1 or 0, non-zero is true.
/// Functions: abs(x) min(a,b) max(a,b) sqrt(x) pow(a,b) clamp(x,lo,hi) if(c,a,b) (names case-insensitive), and
/// time operators prev(x) delta(x) rate(x) avg(x, windowSeconds).
///
/// Evaluation: the output grid is the sorted union of timestamps of the series the expression references.
/// At each grid time every variable takes its sample-and-hold value (no interpolation). NaN = undefined and
/// propagates; division or modulo by zero gives NaN; infinite results become NaN. Time operators act on the GRID
/// (so unrelated series with extra timestamps add grid points):
///   prev(e)  = e at the previous grid time (NaN at the first);  delta(e) = e - prev(e);
///   rate(e)  = delta(e) / seconds since previous grid time;
///   avg(e,w) = time-weighted mean of the held signal e over [max(t-w, firstGridTime), t]; the sample at t itself
///              only starts to count after t (it has zero duration); NaN if the window contains any NaN; at the
///              first grid time it is e itself. w must be a positive numeric literal.
/// </summary>
public static class ExpressionEngine
{
    public const int MaxSourceLength = 4096;
    public const int MaxNestingDepth = 64;

    public static CompiledExpression Compile(string expression)
    {
        if (!TryCompile(expression, out var compiled, out var error))
        {
            throw new ExpressionSyntaxException(error!);
        }
        return compiled!;
    }

    public static bool TryCompile(string? expression, out CompiledExpression? compiled, out ExpressionError? error)
    {
        compiled = null;
        error = null;
        if (expression is null) { error = new ExpressionError("Expression is null.", 0); return false; }
        if (expression.Length > MaxSourceLength)
        {
            error = new ExpressionError($"Expression is longer than {MaxSourceLength} characters.", MaxSourceLength);
            return false;
        }

        try
        {
            var parser = new Parser(expression);
            var root = parser.ParseAll();
            compiled = new CompiledExpression(expression, root, parser.Variables);
            return true;
        }
        catch (ExpressionSyntaxException ex)
        {
            error = ex.Error;
            return false;
        }
    }

    // ---------- AST ----------
    internal abstract record Node(int Pos);
    internal sealed record Num(int Pos, double Value) : Node(Pos);
    internal sealed record Var(int Pos, string Name) : Node(Pos);
    internal sealed record Unary(int Pos, char Op, Node X) : Node(Pos);
    internal sealed record Binary(int Pos, string Op, Node L, Node R) : Node(Pos);
    internal sealed record Call(int Pos, string Name, IReadOnlyList<Node> Args) : Node(Pos);

    // ---------- Parser ----------
    private enum Tok { Num, Ident, Op, LParen, RParen, Comma, End }

    private readonly record struct Token(Tok Kind, string Text, double Number, int Pos, bool Quoted = false);

    private static readonly Dictionary<string, int> Arity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["abs"] = 1, ["min"] = 2, ["max"] = 2, ["sqrt"] = 1, ["pow"] = 2, ["clamp"] = 3, ["if"] = 3,
        ["prev"] = 1, ["delta"] = 1, ["rate"] = 1, ["avg"] = 2
    };

    private sealed class Parser
    {
        private readonly string _s;
        private readonly List<Token> _tokens = [];
        private int _i;
        private int _depth;
        private readonly List<string> _vars = [];

        public Parser(string s)
        {
            _s = s;
            Lex();
        }

        public IReadOnlyList<string> Variables => _vars;

        private static ExpressionSyntaxException Err(string msg, int pos) => new(new ExpressionError(msg, pos));

        private void Lex()
        {
            var p = 0;
            while (p < _s.Length)
            {
                var c = _s[p];
                if (char.IsWhiteSpace(c)) { p++; continue; }

                if (char.IsAsciiDigit(c) || (c == '.' && p + 1 < _s.Length && char.IsAsciiDigit(_s[p + 1])))
                {
                    var start = p;
                    while (p < _s.Length && char.IsAsciiDigit(_s[p])) { p++; }
                    if (p < _s.Length && _s[p] == '.')
                    {
                        p++;
                        while (p < _s.Length && char.IsAsciiDigit(_s[p])) { p++; }
                    }
                    if (p < _s.Length && (_s[p] == 'e' || _s[p] == 'E'))
                    {
                        var q = p + 1;
                        if (q < _s.Length && (_s[q] == '+' || _s[q] == '-')) { q++; }
                        if (q < _s.Length && char.IsAsciiDigit(_s[q]))
                        {
                            while (q < _s.Length && char.IsAsciiDigit(_s[q])) { q++; }
                            p = q;
                        }
                    }
                    if (p < _s.Length && (char.IsAsciiLetter(_s[p]) || _s[p] == '_'))
                    {
                        throw Err("Invalid number literal.", start);
                    }
                    var text = _s[start..p];
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                    {
                        throw Err("Invalid number literal.", start);
                    }
                    _tokens.Add(new Token(Tok.Num, text, d, start));
                    continue;
                }

                if (char.IsAsciiLetter(c) || c == '_')
                {
                    var start = p;
                    while (p < _s.Length && (char.IsAsciiLetterOrDigit(_s[p]) || _s[p] == '_')) { p++; }
                    _tokens.Add(new Token(Tok.Ident, _s[start..p], 0, start));
                    continue;
                }

                if (c == '`')
                {
                    var end = _s.IndexOf('`', p + 1);
                    if (end < 0) { throw Err("Unterminated backtick-quoted name.", p); }
                    if (end == p + 1) { throw Err("Empty quoted name.", p); }
                    _tokens.Add(new Token(Tok.Ident, _s[(p + 1)..end], 0, p, true));
                    p = end + 1;
                    continue;
                }

                switch (c)
                {
                    case '(': _tokens.Add(new Token(Tok.LParen, "(", 0, p)); p++; continue;
                    case ')': _tokens.Add(new Token(Tok.RParen, ")", 0, p)); p++; continue;
                    case ',': _tokens.Add(new Token(Tok.Comma, ",", 0, p)); p++; continue;
                    case '+': case '-': case '*': case '/': case '%':
                        _tokens.Add(new Token(Tok.Op, c.ToString(), 0, p)); p++; continue;
                    case '<': case '>': case '!': case '=':
                        if (p + 1 < _s.Length && _s[p + 1] == '=')
                        {
                            _tokens.Add(new Token(Tok.Op, _s.Substring(p, 2), 0, p)); p += 2; continue;
                        }
                        if (c == '=') { throw Err("Unexpected '='; use '==' for equality.", p); }
                        _tokens.Add(new Token(Tok.Op, c.ToString(), 0, p)); p++; continue;
                    case '&': case '|':
                        if (p + 1 < _s.Length && _s[p + 1] == c)
                        {
                            _tokens.Add(new Token(Tok.Op, _s.Substring(p, 2), 0, p)); p += 2; continue;
                        }
                        throw Err($"Unexpected '{c}'; use '{c}{c}'.", p);
                    default:
                        throw Err($"Unexpected character '{c}'.", p);
                }
            }
            _tokens.Add(new Token(Tok.End, "", 0, _s.Length));
        }

        private Token Cur => _tokens[_i];
        private bool IsOp(params string[] ops) => Cur.Kind == Tok.Op && Array.IndexOf(ops, Cur.Text) >= 0;

        public Node ParseAll()
        {
            if (Cur.Kind == Tok.End) { throw Err("Empty expression.", Cur.Pos); }
            var n = ParseOr();
            if (Cur.Kind != Tok.End)
            {
                throw Err(Cur.Kind == Tok.RParen ? "Unmatched ')'." : $"Unexpected {Describe(Cur)}.", Cur.Pos);
            }
            return n;
        }

        private static string Describe(Token t) => t.Kind switch
        {
            Tok.End => "end of expression",
            Tok.Num => "number",
            Tok.Ident => $"name '{t.Text}'",
            _ => $"'{t.Text}'"
        };

        private Node Binary(Func<Node> next, params string[] ops)
        {
            var left = next();
            while (IsOp(ops))
            {
                var t = Cur;
                _i++;
                var right = next();
                left = new Binary(t.Pos, t.Text, left, right);
            }
            return left;
        }

        private Node ParseOr() => Binary(ParseAnd, "||");
        private Node ParseAnd() => Binary(ParseEq, "&&");
        private Node ParseEq() => Binary(ParseRel, "==", "!=");
        private Node ParseRel() => Binary(ParseAdd, "<", "<=", ">", ">=");
        private Node ParseAdd() => Binary(ParseMul, "+", "-");
        private Node ParseMul() => Binary(ParseUnary, "*", "/", "%");

        private Node ParseUnary()
        {
            if (IsOp("-", "+", "!"))
            {
                var t = Cur;
                _i++;
                if (++_depth > MaxNestingDepth) { throw Err("Expression is nested too deeply.", t.Pos); }
                var x = ParseUnary();
                _depth--;
                return new Unary(t.Pos, t.Text[0], x);
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case Tok.Num:
                    _i++;
                    return new Num(t.Pos, t.Number);
                case Tok.LParen:
                {
                    _i++;
                    if (++_depth > MaxNestingDepth) { throw Err("Expression is nested too deeply.", t.Pos); }
                    var inner = ParseOr();
                    _depth--;
                    if (Cur.Kind != Tok.RParen) { throw Err("Missing ')' to close '('.", t.Pos); }
                    _i++;
                    return inner;
                }
                case Tok.Ident:
                    _i++;
                    if (!t.Quoted && Cur.Kind == Tok.LParen)
                    {
                        return ParseCall(t);
                    }
                    if (!_vars.Contains(t.Text)) { _vars.Add(t.Text); }
                    return new Var(t.Pos, t.Text);
                default:
                    throw Err($"Expected a value but found {Describe(t)}.", t.Pos);
            }
        }

        private Node ParseCall(Token name)
        {
            if (!Arity.TryGetValue(name.Text, out var arity))
            {
                throw Err($"Unknown function '{name.Text}'.", name.Pos);
            }
            var open = Cur;
            _i++;
            var args = new List<Node>();
            if (++_depth > MaxNestingDepth) { throw Err("Expression is nested too deeply.", open.Pos); }
            if (Cur.Kind != Tok.RParen)
            {
                while (true)
                {
                    args.Add(ParseOr());
                    if (Cur.Kind == Tok.Comma) { _i++; continue; }
                    break;
                }
            }
            _depth--;
            if (Cur.Kind != Tok.RParen) { throw Err($"Missing ')' to close call to '{name.Text}'.", open.Pos); }
            _i++;
            if (args.Count != arity)
            {
                throw Err($"Function '{name.Text}' takes {arity} argument(s) but got {args.Count}.", name.Pos);
            }
            if (name.Text.Equals("avg", StringComparison.OrdinalIgnoreCase))
            {
                if (args[1] is not Num { Value: > 0 })
                {
                    throw Err("The window of avg() must be a positive numeric literal (seconds).", args[1].Pos);
                }
            }
            return new Call(name.Pos, name.Text.ToLowerInvariant(), args);
        }
    }

    // ---------- Evaluation ----------
    internal static double[] Eval(Node node, EvalContext ctx)
    {
        var n = ctx.Count;
        switch (node)
        {
            case Num num:
                return Fill(n, num.Value);
            case Var v:
                return ctx.Vars[v.Name];
            case Unary u:
            {
                var x = Eval(u.X, ctx);
                var r = new double[n];
                for (var i = 0; i < n; i++)
                {
                    r[i] = u.Op switch { '-' => -x[i], '+' => x[i], _ => double.IsNaN(x[i]) ? double.NaN : (x[i] != 0 ? 0 : 1) };
                }
                return r;
            }
            case Binary b:
            {
                var l = Eval(b.L, ctx);
                var rr = Eval(b.R, ctx);
                var r = new double[n];
                for (var i = 0; i < n; i++) { r[i] = Bin(b.Op, l[i], rr[i]); }
                return r;
            }
            case Call c:
                return EvalCall(c, ctx);
            default:
                throw new InvalidOperationException("Unknown node.");
        }
    }

    private static double[] Fill(int n, double v)
    {
        var r = new double[n];
        Array.Fill(r, v);
        return r;
    }

    private static double Bool(bool b) => b ? 1d : 0d;

    private static double Bin(string op, double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) { return double.NaN; }
        switch (op)
        {
            case "+": return a + b;
            case "-": return a - b;
            case "*": return a * b;
            case "/": return b == 0 ? double.NaN : a / b;
            case "%": return b == 0 ? double.NaN : a % b;
            case "<": return Bool(a < b);
            case "<=": return Bool(a <= b);
            case ">": return Bool(a > b);
            case ">=": return Bool(a >= b);
            case "==": return Bool(a == b);
            case "!=": return Bool(a != b);
            case "&&": return Bool(a != 0 && b != 0);
            case "||": return Bool(a != 0 || b != 0);
            default: throw new InvalidOperationException(op);
        }
    }

    private static double[] EvalCall(Call c, EvalContext ctx)
    {
        var n = ctx.Count;
        var a = c.Args.Select(x => Eval(x, ctx)).ToArray();
        var r = new double[n];
        switch (c.Name)
        {
            case "abs": for (var i = 0; i < n; i++) { r[i] = Math.Abs(a[0][i]); } break;
            case "sqrt": for (var i = 0; i < n; i++) { r[i] = Math.Sqrt(a[0][i]); } break;
            case "min": for (var i = 0; i < n; i++) { r[i] = Nan2(a[0][i], a[1][i]) ? double.NaN : Math.Min(a[0][i], a[1][i]); } break;
            case "max": for (var i = 0; i < n; i++) { r[i] = Nan2(a[0][i], a[1][i]) ? double.NaN : Math.Max(a[0][i], a[1][i]); } break;
            case "pow": for (var i = 0; i < n; i++) { r[i] = Nan2(a[0][i], a[1][i]) ? double.NaN : Math.Pow(a[0][i], a[1][i]); } break;
            case "clamp":
                for (var i = 0; i < n; i++)
                {
                    double x = a[0][i], lo = a[1][i], hi = a[2][i];
                    r[i] = double.IsNaN(x) || double.IsNaN(lo) || double.IsNaN(hi) || lo > hi ? double.NaN : Math.Min(Math.Max(x, lo), hi);
                }
                break;
            case "if":
                for (var i = 0; i < n; i++)
                {
                    r[i] = double.IsNaN(a[0][i]) ? double.NaN : (a[0][i] != 0 ? a[1][i] : a[2][i]);
                }
                break;
            case "prev":
                for (var i = 1; i < n; i++) { r[i] = a[0][i - 1]; }
                if (n > 0) { r[0] = double.NaN; }
                break;
            case "delta":
                for (var i = 1; i < n; i++) { r[i] = a[0][i] - a[0][i - 1]; }
                if (n > 0) { r[0] = double.NaN; }
                break;
            case "rate":
                for (var i = 1; i < n; i++) { r[i] = (a[0][i] - a[0][i - 1]) / (ctx.Seconds[i] - ctx.Seconds[i - 1]); }
                if (n > 0) { r[0] = double.NaN; }
                break;
            case "avg":
                return Average(a[0], ctx.Seconds, ((Num)c.Args[1]).Value);
            default:
                throw new InvalidOperationException(c.Name);
        }
        return r;
    }

    private static bool Nan2(double a, double b) => double.IsNaN(a) || double.IsNaN(b);

    private static double[] Average(double[] v, double[] t, double window)
    {
        var n = v.Length;
        var r = new double[n];
        if (n == 0) { return r; }
        var integral = new double[n];
        var nanCount = new int[n];
        for (var i = 1; i < n; i++)
        {
            var dt = t[i] - t[i - 1];
            var seg = v[i - 1];
            integral[i] = integral[i - 1] + (double.IsNaN(seg) ? 0 : seg * dt);
            nanCount[i] = nanCount[i - 1] + (double.IsNaN(seg) ? 1 : 0);
        }

        for (var i = 0; i < n; i++)
        {
            var s = Math.Max(t[i] - window, t[0]);
            var j = Array.BinarySearch(t, s);
            if (j < 0) { j = ~j - 1; }
            if (j >= i) { r[i] = v[i]; continue; }
            if (double.IsNaN(v[j]) || nanCount[i] - nanCount[j + 1] > 0) { r[i] = double.NaN; continue; }
            r[i] = (integral[i] - integral[j] - v[j] * (s - t[j])) / (t[i] - s);
        }
        return r;
    }

    internal sealed class EvalContext
    {
        public required int Count { get; init; }
        public required double[] Seconds { get; init; }
        public required Dictionary<string, double[]> Vars { get; init; }
    }

    public sealed class CompiledExpression
    {
        private readonly Node _root;

        internal CompiledExpression(string source, Node root, IReadOnlyList<string> required)
        {
            Source = source;
            _root = root;
            RequiredVariables = required.ToArray();
        }

        public string Source { get; }

        /// <summary>Distinct variable names in order of first appearance.</summary>
        public IReadOnlyList<string> RequiredVariables { get; }

        /// <summary>Evaluates on the union of the required series' timestamps (see <see cref="ExpressionEngine"/>).
        /// Extra inputs are ignored. A missing required variable throws <see cref="ArgumentException"/>.</summary>
        public SampleSeries Evaluate(IReadOnlyDictionary<string, SampleSeries> inputs)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            var missing = RequiredVariables.Where(v => !inputs.ContainsKey(v)).ToArray();
            if (missing.Length > 0)
            {
                throw new ArgumentException("Missing input series: " + string.Join(", ", missing), nameof(inputs));
            }

            var grid = new SortedSet<long>();
            foreach (var name in RequiredVariables)
            {
                grid.UnionWith(inputs[name].Timestamps);
            }

            var ts = grid.ToArray();
            var seconds = new double[ts.Length];
            for (var i = 0; i < ts.Length; i++) { seconds[i] = (ts[i] - ts[0]) / 1e9; }

            var vars = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var name in RequiredVariables)
            {
                var s = inputs[name];
                var vals = new double[ts.Length];
                var k = -1;
                for (var i = 0; i < ts.Length; i++)
                {
                    while (k + 1 < s.Count && s.Timestamps[k + 1] <= ts[i]) { k++; }
                    vals[i] = k < 0 ? double.NaN : s.Values[k];
                }
                vars[name] = vals;
            }

            var result = Eval(_root, new EvalContext { Count = ts.Length, Seconds = seconds, Vars = vars });
            for (var i = 0; i < result.Length; i++)
            {
                if (double.IsInfinity(result[i])) { result[i] = double.NaN; }
            }
            return new SampleSeries(ts, result);
        }
    }
}
