using Xunit;
using RbgCanInsight.Analysis.Derived;

namespace RbgCanInsight.Tests;

public sealed class ExpressionEngineTests
{
    private const long S = 1_000_000_000;

    private static SampleSeries Series(params (long Ns, double V)[] p) =>
        new(p.Select(x => x.Ns).ToArray(), p.Select(x => x.V).ToArray());

    private static double Const(string expr)
    {
        var r = ExpressionEngine.Compile(expr)
            .Evaluate(new Dictionary<string, SampleSeries>());
        Assert.Empty(r.Values);
        // constants have no grid; evaluate via a dummy clock series instead
        var c = ExpressionEngine.Compile("0*t + (" + expr + ")")
            .Evaluate(new Dictionary<string, SampleSeries> { ["t"] = Series((0, 1)) });
        return c.Values[0];
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]
    [InlineData("2 * 3 % 4", 2)]
    [InlineData("-2 * -3", 6)]
    [InlineData("1 + 2 < 4 && 5 > 4", 1)]
    [InlineData("1 || 0 && 0", 1)]
    [InlineData("1 == 1 + 0", 1)]
    [InlineData("!0 + 1", 2)]
    [InlineData("1.5e1 + .5", 15.5)]
    [InlineData("abs(-3) + max(1, 2) + min(4, 5)", 9)]
    [InlineData("sqrt(16) + pow(2, 10)", 1028)]
    [InlineData("clamp(15, 0, 10)", 10)]
    [InlineData("if(1 > 2, 10, 20)", 20)]
    [InlineData("IF(1, 10, 20)", 10)]
    public void PrecedenceAndFunctions(string expr, double expected) =>
        Assert.Equal(expected, Const(expr), 9);

    [Theory]
    [InlineData("1 +", 3)]
    [InlineData("(1 + 2", 0)]
    [InlineData("1 + 2)", 5)]
    [InlineData("1 $ 2", 2)]
    [InlineData("a = 1", 2)]
    [InlineData("foo(1)", 0)]
    [InlineData("abs(1, 2)", 0)]
    [InlineData("avg(x, y)", 7)]
    [InlineData("avg(x, 0)", 7)]
    [InlineData("1.2.3", 3)]
    [InlineData("2x", 0)]
    [InlineData("", 0)]
    [InlineData("1 2", 2)]
    [InlineData("x & y", 2)]
    public void ErrorsCarryCharacterPosition(string expr, int position)
    {
        var ex = Assert.Throws<ExpressionSyntaxException>(() => ExpressionEngine.Compile(expr));
        Assert.Equal(position, ex.Position);
        Assert.False(ExpressionEngine.TryCompile(expr, out var c, out var err));
        Assert.Null(c);
        Assert.Equal(position, err!.Position);
    }

    [Theory]
    [InlineData("System.IO.File.Delete(x)")]
    [InlineData("x.GetType()")]
    [InlineData("typeof(x)")]
    [InlineData("new Foo()")]
    [InlineData("x; y")]
    [InlineData("\"a\" + x")]
    [InlineData("x[0]")]
    [InlineData("System.IO")]
    public void InjectionAttemptsAreSyntaxErrors(string expr) =>
        Assert.Throws<ExpressionSyntaxException>(() => ExpressionEngine.Compile(expr));

    [Fact]
    public void SystemIoReportsTheDotPosition()
    {
        var ex = Assert.Throws<ExpressionSyntaxException>(() => ExpressionEngine.Compile("System.IO"));
        Assert.Equal(6, ex.Position);
    }

    [Fact]
    public void DeepNestingIsRejectedNotStackOverflow()
    {
        var s = new string('(', 200) + "1" + new string(')', 200);
        Assert.Throws<ExpressionSyntaxException>(() => ExpressionEngine.Compile(s));
        Assert.Throws<ExpressionSyntaxException>(() => ExpressionEngine.Compile(new string('-', 5000) + "1"));
    }

    [Fact]
    public void RequiredVariables_AreDistinctInOrderAndSupportBackticks()
    {
        var c = ExpressionEngine.Compile("a + `Msg.Sig` * a + b");
        Assert.Equal(["a", "Msg.Sig", "b"], c.RequiredVariables);
    }

    [Fact]
    public void SampleAndHold_UnionTimestampsAndUndefinedBeforeFirstSample()
    {
        var a = Series((1 * S, 1), (3 * S, 3));
        var b = Series((2 * S, 10), (3 * S, 30), (4 * S, 40));
        var r = ExpressionEngine.Compile("a + b").Evaluate(
            new Dictionary<string, SampleSeries> { ["a"] = a, ["b"] = b });
        Assert.Equal(new long[] { 1 * S, 2 * S, 3 * S, 4 * S }, r.Timestamps.ToArray());
        Assert.True(double.IsNaN(r.Values[0])); // b undefined
        Assert.Equal(11, r.Values[1]);          // a held from 1s
        Assert.Equal(33, r.Values[2]);
        Assert.Equal(43, r.Values[3]);          // a held from 3s
    }

    [Fact]
    public void MissingInputThrows() =>
        Assert.Throws<ArgumentException>(() =>
            ExpressionEngine.Compile("a + b").Evaluate(new Dictionary<string, SampleSeries> { ["a"] = Series((0, 1)) }));

    [Fact]
    public void DivisionAndModuloByZero_AreNaN()
    {
        var x = Series((0, 1), (S, 2));
        var z = Series((0, 0), (S, 2));
        var r = ExpressionEngine.Compile("x / z").Evaluate(new Dictionary<string, SampleSeries> { ["x"] = x, ["z"] = z });
        Assert.True(double.IsNaN(r.Values[0]));
        Assert.Equal(1, r.Values[1]);
        var m = ExpressionEngine.Compile("x % z").Evaluate(new Dictionary<string, SampleSeries> { ["x"] = x, ["z"] = z });
        Assert.True(double.IsNaN(m.Values[0]));
        Assert.Equal(0, m.Values[1]);
        Assert.True(double.IsNaN(Const("sqrt(-1)")));
        Assert.True(double.IsNaN(Const("pow(10, 1000)"))); // infinite -> NaN
    }

    [Fact]
    public void PrevDeltaRate()
    {
        var x = Series((0, 10), (2 * S, 14), (3 * S, 14), (7 * S, 22));
        var inputs = new Dictionary<string, SampleSeries> { ["x"] = x };
        var prev = ExpressionEngine.Compile("prev(x)").Evaluate(inputs);
        Assert.True(double.IsNaN(prev.Values[0]));
        Assert.Equal([10, 14, 14], prev.Values.Skip(1));

        var delta = ExpressionEngine.Compile("delta(x)").Evaluate(inputs);
        Assert.True(double.IsNaN(delta.Values[0]));
        Assert.Equal([4, 0, 8], delta.Values.Skip(1));

        var rate = ExpressionEngine.Compile("rate(x)").Evaluate(inputs);
        Assert.True(double.IsNaN(rate.Values[0]));
        Assert.Equal([2, 0, 2], rate.Values.Skip(1));
    }

    [Fact]
    public void Avg_IsTimeWeightedOverHeldSignal()
    {
        // x = 0 on [0,1), 10 on [1,3), 20 from 3 on. Grid: 0,1,3,4.
        var x = Series((0, 0), (1 * S, 10), (3 * S, 20), (4 * S, 20));
        var r = ExpressionEngine.Compile("avg(x, 2)").Evaluate(new Dictionary<string, SampleSeries> { ["x"] = x });
        Assert.Equal(0, r.Values[0]);                // first grid point is the value itself
        Assert.Equal(0, r.Values[1]);                // window [0,1] holds 0
        Assert.Equal(10, r.Values[2], 9);            // window [1,3] holds 10
        Assert.Equal(15, r.Values[3], 9);            // window [2,4]: 10 for 1 s, 20 for 1 s
    }

    [Fact]
    public void Avg_WithUndefinedInWindowIsNaN()
    {
        var a = Series((0, 1), (2 * S, 1));
        var b = Series((1 * S, 5), (3 * S, 5));
        var r = ExpressionEngine.Compile("avg(a + b, 1.5)").Evaluate(
            new Dictionary<string, SampleSeries> { ["a"] = a, ["b"] = b });
        // a+b is NaN on [0,1) (b undefined) and 6 afterwards.
        Assert.All(r.Values.Take(3), v => Assert.True(double.IsNaN(v)));
        Assert.Equal(6, r.Values[3], 9); // t=3: window [1.5,3] no longer touches the NaN segment
    }

    [Fact]
    public void InvalidSeriesRejected()
    {
        Assert.Throws<ArgumentException>(() => Series((2, 1), (1, 2)));
        Assert.Throws<ArgumentException>(() => new SampleSeries([1L], [1.0, 2.0]));
    }
}
