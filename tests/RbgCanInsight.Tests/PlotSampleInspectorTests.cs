using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class PlotSampleInspectorTests
{
    private static readonly SignalSample[] Recorded =
    [
        new(1_000_000_000L, 10, 4),
        new(1_100_000_000L, 10, 5), // same physical value; NOT a change
        new(1_200_000_000L, 18, 6), // first observed transition
        new(1_300_000_000L, 18, 7),
        new(1_400_000_000L, 13, 8), // second observed transition
        new(1_400_000_000L, 20, 9), // same timestamp, DISTINCT source frame
        new(1_600_000_000L, 20, 10)
    ];

    [Fact]
    public void LastValueIsNeverBorrowedFromFutureFrames()
    {
        var missing = PlotSampleInspector.Inspect(Recorded, 800_000_000L, 0);
        Assert.Null(missing.LastObserved);
        Assert.False(missing.HasChange);
        Assert.Null(missing.ObservationAgeNanoseconds);

        var held = PlotSampleInspector.Inspect(Recorded, 1_150_000_000L, 1_000_000L);
        Assert.Equal(10d, held.LastObserved?.Value);
        Assert.Equal(5, held.LastObserved?.FrameIndex);
        Assert.Equal(50_000_000L, held.ObservationAgeNanoseconds);
        Assert.False(held.AtOriginalSample);
        Assert.False(held.HasChange);
        Assert.Equal("1.150000000 s", held.CursorTimeText);
        Assert.Contains("frame #6", held.FrameText);
    }

    [Fact]
    public void HoverNearRecordedChangeReportsExactAdjacentSourceFrameDeltaAndTime()
    {
        var readout = PlotSampleInspector.Inspect(Recorded,
            1_198_000_000L, 10_000_000L);
        Assert.Equal(10d, readout.LastObserved?.Value);
        Assert.True(readout.HasChange);
        Assert.True(readout.CandidateChangeIsAfterCursor);
        Assert.Equal(5, readout.ChangeBefore?.FrameIndex);
        Assert.Equal(6, readout.ChangeAfter?.FrameIndex);
        Assert.Equal(8d, readout.Delta);
        Assert.Equal("1.200000000 s", readout.ChangeTimeText);
        Assert.Equal("frame #7", readout.ChangeFrameText);
    }

    [Fact]
    public void EqualTimeSamplesRemainDistinctAndShowActualLastSourceFrame()
    {
        var readout = PlotSampleInspector.Inspect(Recorded,
            1_400_000_000L, 0);
        Assert.True(readout.AtOriginalSample);
        Assert.Equal(20d, readout.LastObserved?.Value);
        Assert.Equal(9, readout.LastObserved?.FrameIndex);
        Assert.Equal(7d, readout.Delta);
        Assert.Equal(8, readout.ChangeBefore?.FrameIndex);
        Assert.Equal(9, readout.ChangeAfter?.FrameIndex);
    }

    [Fact]
    public void RawAndDbcStateUseTheExactLastObservedFrameAtDuplicateTimestamp()
    {
        var signal = new DbcSignal("State", 0, 8, DbcByteOrder.Intel, false,
            1, 0, 0, 255, "", "ECU", ValueDescriptions: new Dictionary<long, string>
            {
                [13] = "Intermediate",
                [20] = "Ready"
            });

        var readout = PlotSampleInspector.Inspect(Recorded, 1_400_000_000L,
            nearbyChangeNanoseconds: 0, signal: signal);

        Assert.Equal(20d, readout.LastObserved?.Value);
        Assert.Equal(9, readout.LastObserved?.FrameIndex);
        Assert.Equal(20d, readout.RawValue);
        Assert.Equal("Ready", readout.DbcState);
        Assert.Equal(readout.ChangeBefore?.TimestampNanoseconds, 1_400_000_000L);
        Assert.Equal(readout.ChangeAfter?.TimestampNanoseconds, 1_400_000_000L);
        Assert.Equal("1.400000000 s", readout.LastObservedTimeText);
    }

    [Fact]
    public void UnsupportedFloatConversionKeepsPhysicalValueButDoesNotInventRawOrState()
    {
        var signal = new DbcSignal("Temperature", 0, 32, DbcByteOrder.Intel, false,
            0.1, 0, 0, 0, "C", "ECU", ValueType: DbcSignalValueType.Float32,
            ValueDescriptions: new Dictionary<long, string> { [20] = "Warm" });

        var readout = PlotSampleInspector.Inspect(Recorded, 1_150_000_000L,
            nearbyChangeNanoseconds: 0, signal: signal);

        Assert.Equal(10d, readout.LastObserved?.Value);
        Assert.Null(readout.RawValue);
        Assert.Null(readout.DbcState);
    }

    [Fact]
    public void ChangeReadoutExposesBothSourceTimesForHeldCursor()
    {
        var readout = PlotSampleInspector.Inspect(Recorded,
            1_198_000_000L, nearbyChangeNanoseconds: 10_000_000L);

        Assert.Equal("1.100000000 s", readout.ChangeBeforeTimeText);
        Assert.Equal("1.200000000 s", readout.ChangeAfterTimeText);
        Assert.Equal("1.198000000 s", readout.CursorTimeText);
        Assert.Equal("1.100000000 s", readout.LastObservedTimeText);
        Assert.Equal(readout.LastObservedTimeText, readout.ObservedTimeText);
    }

    [Fact]
    public void TimestampFormattingPreservesNanosecondsAndHandlesLongExtremes()
    {
        var fractional = PlotSampleInspector.Inspect(
            [new SignalSample(1_234, 1, 0)], 1_234, 0);
        Assert.Equal("0.000001234 s", fractional.CursorTimeText);
        Assert.Equal("0.000001234 s", fractional.LastObservedTimeText);

        var extreme = PlotSampleInspector.Inspect(
            [new SignalSample(long.MinValue, 1, 0)], long.MaxValue, 0);
        Assert.Equal("-9223372036.854775808 s", extreme.LastObservedTimeText);
        Assert.Equal("9223372036.854775807 s", extreme.CursorTimeText);
        Assert.Equal(long.MaxValue, extreme.ObservationAgeNanoseconds);
    }

    [Fact]
    public void NearbyChangeToleranceRemainsNanosecondExactAtLargeTimestamps()
    {
        var samples = new[]
        {
            new SignalSample(long.MaxValue - 3, 10, 1),
            new SignalSample(long.MaxValue - 1, 20, 2)
        };

        var readout = PlotSampleInspector.Inspect(samples,
            long.MaxValue - 2, nearbyChangeNanoseconds: 0);

        Assert.Equal(10d, readout.LastObserved?.Value);
        Assert.False(readout.HasChange);
    }

    [Fact]
    public void DenseTraceSearchIsBoundedToNearbyOriginalSamples()
    {
        var samples = Enumerable.Range(0, 1_000_000)
            .Select(i => new SignalSample(i * 1_000_000L, i % 2, i)).ToArray();
        var near = PlotSampleInspector.Inspect(samples, 650_000_000_000L,
            nearbyChangeNanoseconds: 2_000_000L, maxChangeSamples: 32);
        Assert.True(near.HasChange);
        Assert.InRange(near.ChangeAfter!.Value.FrameIndex, 649_998, 650_002);
        Assert.Equal(650_000, near.LastObserved?.FrameIndex);
        Assert.Equal(0L, near.ObservationAgeNanoseconds);
    }

    [Fact]
    public void VisibleChangeDotsRequireAdjacentOriginalSourceFrameTransition()
    {
        // A decimator can omit a middle original value. Comparing the
        // displayed points 10 -> 18 would invent a change at its timestamp
        // if the source had already changed at an omitted frame.
        var all = new SignalSample[]
        {
            new(0, 10, 10), new(1, 18, 11),
            new(2, 18, 12), new(3, 25, 13),
            new(4, 25, 14), new(5, 30, 15)
        };
        var visible = new[] { all[0], all[2], all[3], all[4], all[5] };
        var dots = PlotSampleInspector.ChangeDots(all, visible);
        Assert.Equal(new[] { 13, 15 }, dots.Select(point => point.FrameIndex));
        Assert.DoesNotContain(dots, point => point.FrameIndex == 12);

        // A small cap must thin real source changes, never synthesize any.
        var thinned = PlotSampleInspector.ChangeDots(all,
            new[] { all[1], all[3], all[5] }, maxDots: 2);
        Assert.Equal(2, thinned.Length);
        Assert.All(thinned, point =>
            Assert.Contains(point.FrameIndex, new[] { 11, 13, 15 }));
    }

    [Fact]
    public void DuplicateTimestampDotsKeepActualSourceFrameIdentity()
    {
        var dots = PlotSampleInspector.ChangeDots(Recorded,
            new[] { Recorded[4], Recorded[5] });
        Assert.Equal(new[] { 8, 9 }, dots.Select(point => point.FrameIndex));
    }

    [Fact]
    public void DuplicateTimestampDotLookupFindsExactFrameBeyondFormerScanLimit()
    {
        var samples = Enumerable.Range(0, 40)
            .Select(i => new SignalSample(1_000, i == 0 ? 0 : 1, i))
            .ToArray();

        var dots = PlotSampleInspector.ChangeDots(samples, [samples[0]]);

        Assert.Empty(dots); // The first sample has no preceding source frame.
        var transitionDots = PlotSampleInspector.ChangeDots(samples, [samples[1]]);
        Assert.Equal(new[] { 1 }, transitionDots.Select(point => point.FrameIndex));
    }

    [Fact]
    public void ConstantSignalsAndInvalidInputDoNotInventTransitions()
    {
        var constant = new SignalSample[]
        {
            new(0, 5, 10), new(1, 5, 11), new(2, 5, 12)
        };
        Assert.False(PlotSampleInspector.Inspect(constant, 2, 10).HasChange);
        Assert.False(PlotSampleInspector.Inspect([], 10, 10).HasChange);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlotSampleInspector.Inspect(constant, 10, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlotSampleInspector.Inspect(constant, 10, 1, maxChangeSamples: 1));
    }
}
