using RbgCanInsight.Analysis;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceStatisticsSourceIsolationTests
{
    [Fact]
    public void InterleavedIndependentLoggersDoNotGenerateFalseZeroPeriodBursts()
    {
        var frames = Enumerable.Range(0, 5).SelectMany(second => new[]
        {
            new CanFrame(0, 0x100, [(byte)(10 + second)],
                second * 1_000_000_000L),
            new CanFrame(0, 0x100, [(byte)(100 + second)],
                second * 1_000_000_000L)
        }).ToArray();
        int SourceAt(int index) => index % 2;
        var results = TraceStatistics.ComputeBySource(frames, SourceAt)
            .OrderBy(row => row.SourceIndex).ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(new[] { 0, 1 }, results.Select(row => row.SourceIndex));
        Assert.All(results, item =>
        {
            Assert.Equal(5, item.Row.FrameCount);
            Assert.Equal(1_000d, item.Row.AveragePeriodMilliseconds, 6);
            Assert.Equal(1_000d, item.Row.MinPeriodMilliseconds, 6);
            Assert.Equal(1_000d, item.Row.MaxPeriodMilliseconds, 6);
            Assert.Equal(0, item.Row.BurstCount);
        });
        Assert.Empty(TraceStatistics.FindTimingIncidentsBySource(
            frames, results, SourceAt));
        // Legacy combined statistics intentionally remain a separate API;
        // do not reuse them to claim per-logger timing correctness.
        var blended = Assert.Single(TraceStatistics.Compute(frames));
        Assert.Equal(10, blended.FrameCount);
        Assert.NotEqual(1_000d, blended.AveragePeriodMilliseconds);
    }

    [Fact]
    public void SingleLoggerGapReferencesExactOriginalFrameWithoutForeignSource()
    {
        var clockA = new[] { 0L, 1_000_000_000L, 5_000_000_000L, 6_000_000_000L };
        var clockB = new[] { 0L, 1_000_000_000L, 2_000_000_000L, 3_000_000_000L };
        var frames = Enumerable.Range(0, 4).SelectMany(index => new[]
        {
            new CanFrame(0, 0x100, [(byte)(10 + index)], clockA[index]),
            new CanFrame(0, 0x100, [(byte)(80 + index)], clockB[index])
        }).ToArray();
        int SourceAt(int index) => index % 2;
        var rows = TraceStatistics.ComputeBySource(frames, SourceAt);
        var incident = Assert.Single(TraceStatistics.FindTimingIncidentsBySource(
            frames, rows, SourceAt, ratioThreshold: 1.8));
        Assert.Equal(0, incident.SourceIndex);
        Assert.Equal(4, incident.Incident.FrameIndex);
        Assert.Equal(4_000d, incident.Incident.GapMilliseconds, 6);
        Assert.Equal(0, SourceAt(incident.Incident.FrameIndex));
    }

    [Fact]
    public void InvalidLoggerIndicesAndThresholdsAreRejected()
    {
        var frames = new[] { new CanFrame(0, 256, [10], 0) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TraceStatistics.ComputeBySource(frames, _ => -1));
        var rows = TraceStatistics.ComputeBySource(frames, _ => 0);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TraceStatistics.FindTimingIncidentsBySource(
                frames, rows, _ => 0, ratioThreshold: double.NaN));
    }
}
