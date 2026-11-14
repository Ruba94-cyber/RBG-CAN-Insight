using RbgCanInsight.Analysis.Derived;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DerivedSeriesToolsTests
{
    private const long S = 1_000_000_000;

    [Fact]
    public void StatsIgnoreNanAndCountIt()
    {
        var series = new SampleSeries([0, S, 2 * S, 3 * S], [2, double.NaN, 8, 5]);
        var stats = DerivedSeriesStats.Compute(series);
        Assert.Equal(4, stats.SampleCount);
        Assert.Equal(1, stats.NanCount);
        Assert.Equal(2, stats.Min);
        Assert.Equal(8, stats.Max);
        Assert.Equal(5, stats.Mean);
        Assert.Contains("Max: 8", stats.ToText());
    }

    [Fact]
    public void StatsOfAllNanHaveNoFiniteValues()
    {
        var stats = DerivedSeriesStats.Compute(new SampleSeries([0], [double.NaN]));
        Assert.Equal(0, stats.FiniteCount);
        Assert.Contains("no finite values", stats.ToText());
    }

    [Fact]
    public void TimelineIsMarkedDerivedAndDropsNan()
    {
        var series = new SampleSeries([0, S, 2 * S], [1, double.NaN, 3]);
        var timeline = DerivedSeriesTools.ToTimeline("a * 2", series);
        Assert.Equal("Derived: a * 2", timeline.Signal.Name);
        Assert.Contains("not recorded", timeline.DatabaseName);
        Assert.Equal(2, timeline.Samples.Count);
        Assert.All(timeline.Samples, s => Assert.Equal(-1, s.FrameIndex));
        Assert.Equal(DerivedSeriesTools.ToSampleSeries(timeline).Values.Length, 2);
    }

    [Fact]
    public void CsvHasHeaderAndInvariantRows()
    {
        var csv = DerivedSeriesTools.ToCsv("a * 2", new SampleSeries([5 * S, 6 * S], [1.5, double.NaN]));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# Expression: a * 2", lines[1]);
        Assert.Equal("timestamp_ns,time_s,value", lines[2]);
        Assert.Equal("5000000000,0,1.5", lines[3]);
        Assert.Equal("6000000000,1,NaN", lines[4]);
    }

    [Fact]
    public void EndToEndWithEngine()
    {
        var input = new SampleSeries([0, S], [10, 40]);
        var result = ExpressionEngine.Compile("a * 2").Evaluate(new Dictionary<string, SampleSeries> { ["a"] = input });
        Assert.Equal(80, DerivedSeriesStats.Compute(result).Max);
    }
}
