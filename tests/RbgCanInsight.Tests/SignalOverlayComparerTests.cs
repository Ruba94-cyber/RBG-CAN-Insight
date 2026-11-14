using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalOverlayComparerTests
{
    private static SignalSample[] Series(long startMs, long periodMs, params double[] values) =>
        values.Select((value, index) => new SignalSample((startMs + index * periodMs) * 1_000_000, value, index)).ToArray();

    [Fact]
    public void IdenticalSignalsAlignedByTheirOwnStartAreIdenticalEvenWhenRecordedAtDifferentTimes()
    {
        var reference = Series(1_000, 100, 1, 2, 3, 4);
        var subject = Series(50_000, 100, 1, 2, 3, 4);              // same data, recorded much later
        var result = SignalOverlayComparer.Compare(reference, subject,
            new OverlayOrigins(1_000 * 1_000_000L, 50_000 * 1_000_000L));
        Assert.Equal(SignalOverlayVerdict.Identical, result.Verdict);
        Assert.Equal(4, result.ComparedSamples);
        Assert.Equal(0, result.MaxAbsDelta);
        Assert.Equal(1.0, result.Coverage);
        Assert.Contains("No interpolation", result.Alignment);
    }

    [Fact]
    public void ConstantOffsetIsReportedWithSignedMeanAndProvingFrames()
    {
        var reference = Series(0, 100, 10, 10, 10, 10);
        var subject = Series(0, 100, 10, 15, 10, 10);                // one sample differs by +5
        var result = SignalOverlayComparer.Compare(reference, subject, new OverlayOrigins(0, 0));
        Assert.Equal(SignalOverlayVerdict.Different, result.Verdict);
        Assert.Equal(5, result.MaxAbsDelta);
        Assert.Equal(1.25, result.MeanAbsDelta);
        Assert.Equal(1.25, result.MeanDelta);
        Assert.Equal(Math.Sqrt(25.0 / 4), result.RootMeanSquareDelta, 12);
        var largest = Assert.IsType<SignalOverlayDifference>(result.Largest);
        Assert.Equal(1, largest.SubjectFrameIndex);
        Assert.Equal(1, largest.ReferenceFrameIndex);
        Assert.Equal(0.1, largest.RelativeSeconds, 9);
        Assert.Equal(10, largest.ReferenceValue);
        Assert.Equal(15, largest.SubjectValue);
        Assert.Equal(5, largest.Delta);
    }

    [Fact]
    public void ToleranceSeparatesWithinToleranceFromDifferent()
    {
        var reference = Series(0, 100, 10, 10, 10);
        var subject = Series(0, 100, 10, 10.4, 10);
        Assert.Equal(SignalOverlayVerdict.WithinTolerance,
            SignalOverlayComparer.Compare(reference, subject, new OverlayOrigins(0, 0), valueTolerance: 0.5).Verdict);
        Assert.Equal(SignalOverlayVerdict.Different,
            SignalOverlayComparer.Compare(reference, subject, new OverlayOrigins(0, 0), valueTolerance: 0.1).Verdict);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignalOverlayComparer.Compare(reference, subject, new OverlayOrigins(0, 0), valueTolerance: -1));
    }

    [Fact]
    public void StepHoldUsesTheLastReferenceSampleAtOrBeforeTheSubjectTime()
    {
        var reference = Series(0, 100, 1, 2, 3);                      // 0, 100, 200 ms
        var subject = new[]
        {
            new SignalSample(150_000_000, 2, 7),                       // reference 2 (from 100 ms) holds
            new SignalSample(200_000_000, 3, 8)                        // exact match
        };
        var result = SignalOverlayComparer.Compare(reference, subject, new OverlayOrigins(0, 0));
        Assert.Equal(SignalOverlayVerdict.Identical, result.Verdict);
        Assert.Equal([7, 8], result.LargestDifferences.Select(d => d.SubjectFrameIndex).Order().ToArray());
        Assert.Equal(1, result.LargestDifferences.Single(d => d.SubjectFrameIndex == 7).ReferenceFrameIndex);
    }

    [Fact]
    public void SamplesWithoutARecentReferenceAreCountedAsUncomparedNotInvented()
    {
        var reference = Series(1_000, 100, 1, 2, 3);                  // 1.0 .. 1.2 s
        var subject = new[]
        {
            new SignalSample(500_000_000, 9, 0),                       // before the first reference sample
            new SignalSample(1_100_000_000, 2, 1),                     // compared
            new SignalSample(60_000_000_000, 9, 2)                     // far later: reference too old (hold limit 1 s)
        };
        var result = SignalOverlayComparer.Compare(reference, subject, new OverlayOrigins(0, 0));
        Assert.Equal(1, result.ComparedSamples);
        Assert.Equal(2, result.UncomparedSamples);
        Assert.Equal(1.0 / 3, result.Coverage, 9);
        Assert.Equal(1000, result.HoldLimitMilliseconds);
        Assert.Equal(SignalOverlayVerdict.Identical, result.Verdict);
    }

    [Fact]
    public void NoOverlapIsReportedExplicitly()
    {
        var result = SignalOverlayComparer.Compare(Series(0, 100, 1, 2), Series(100_000, 100, 1, 2), new OverlayOrigins(0, 0));
        Assert.Equal(SignalOverlayVerdict.NoOverlap, result.Verdict);
        Assert.Equal(0, result.ComparedSamples);
        Assert.Null(result.Largest);
        Assert.Equal(0, SignalOverlayComparer.Compare([], [], new OverlayOrigins(0, 0)).Coverage);
    }

    [Fact]
    public void SingleReferenceSampleOnlyMatchesAnExactTimestamp()
    {
        var reference = new[] { new SignalSample(100_000_000, 5, 0) };
        var subject = new[] { new SignalSample(100_000_000, 5, 0), new SignalSample(101_000_000, 5, 1) };
        var result = SignalOverlayComparer.Compare(reference, subject, new OverlayOrigins(0, 0));
        Assert.Equal(1, result.ComparedSamples);
        Assert.Equal(1, result.UncomparedSamples);
    }

    [Fact]
    public void LargestDifferencesAreOrderedByMagnitudeAndBounded()
    {
        var reference = Series(0, 10, Enumerable.Repeat(0.0, 50).ToArray());
        var values = Enumerable.Range(0, 50).Select(i => (double)(i % 7)).ToArray();
        var result = SignalOverlayComparer.Compare(reference, Series(0, 10, values), new OverlayOrigins(0, 0), largestCount: 5);
        Assert.Equal(5, result.LargestDifferences.Count);
        Assert.True(result.LargestDifferences.Zip(result.LargestDifferences.Skip(1),
            (a, b) => Math.Abs(a.Delta) >= Math.Abs(b.Delta)).All(ok => ok));
        Assert.Equal(6, result.LargestDifferences[0].Delta);
    }

    [Fact]
    public void ChartDecimationKeepsEveryPeakAndBoundsPointCount()
    {
        var values = Enumerable.Range(0, 100_000).Select(i => i == 54_321 ? 1000.0 : Math.Sin(i / 50.0)).ToArray();
        var subject = Series(0, 1, values);
        var result = SignalOverlayComparer.Compare(subject, subject, new OverlayOrigins(0, 0), chartPoints: 400);
        Assert.True(result.SubjectChart.Count <= 402);
        Assert.Contains(result.SubjectChart, point => point.Value == 1000.0);          // the spike survives
        Assert.Equal(100_000, result.SubjectSamples);
        Assert.Equal(SignalOverlayVerdict.Identical, result.Verdict);
    }
}
