using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalChangeWitnessFinderTests
{
    private static (SignalTimeline A, SignalTimeline B, SignalTimeline? OtherLogger,
        Func<int, int> SourceAt) Fixture()
    {
        var dbc = new DbcParser().Parse("changes", new StringReader(
            """
            BO_ 256 Requested: 8 ECM
             SG_ RequestedTorque : 0|8@1+ (1,0) [0|255] "Nm" ECM
            BO_ 257 Measured: 8 ECM
             SG_ MeasuredTorque : 0|8@1+ (1,0) [0|255] "Nm" ECM
            """));
        var frames = new List<CanFrame>();
        var sourceIndices = new List<int>();
        void Put(uint id, byte value, long t, int logger)
        {
            frames.Add(new CanFrame(0, id, [value, 0, 0, 0, 0, 0, 0, 0], t));
            sourceIndices.Add(logger);
        }
        for (var i = 0; i < 26; i++)
        {
            var t = 1_000_000_000L + i * 500_000_000L;
            Put(256, (byte)(20 + i * 3), t, 0);
            Put(257, (byte)(50 + i * 2), t + 120_000_000L, 0);
            // Same wire ID in unrelated physical recorder must be isolated.
            Put(256, (byte)(90 - i), t + 10_000_000L, 1);
        }
        int At(int idx) => (uint)idx < (uint)sourceIndices.Count ? sourceIndices[idx] : -1;
        var timelines = SignalTimelineBuilder.Build(frames, [dbc],
            sourceAt: At, sourcePaths: ["first.asc", "second.asc"]);
        var a = Assert.Single(timelines.Where(t => t.Signal.Name == "RequestedTorque" &&
            t.OriginalSourceIndex == 0));
        var b = Assert.Single(timelines.Where(t => t.Signal.Name == "MeasuredTorque" &&
            t.OriginalSourceIndex == 0));
        var other = Assert.Single(timelines.Where(t => t.Signal.Name == "RequestedTorque" &&
            t.OriginalSourceIndex == 1));
        return (a, b, other, At);
    }

    [Fact]
    public void EveryMatchedObservedChangeHasFourOriginalSameLoggerFramesAndNoReusedCandidate()
    {
        var (a, b, _, sourceAt) = Fixture();
        var report = SignalChangeWitnessFinder.Find(a, b, sourceAt,
            maxDistanceNanoseconds: 250_000_000L);
        Assert.Null(report.Limitation);
        // The common recorded-time window starts after the first reference
        // sample and ends before the final candidate transition. Those two
        // boundary changes do not have both original frames inside the
        // shared window, so only the 23 interior changes are pairable.
        Assert.Equal(23, report.Witnesses.Count);
        Assert.False(report.Truncated);
        var used = new HashSet<int>();
        foreach (var witness in report.Witnesses)
        {
            Assert.Equal(0, sourceAt(witness.ReferenceBefore.FrameIndex));
            Assert.Equal(0, sourceAt(witness.ReferenceAfter.FrameIndex));
            Assert.Equal(0, sourceAt(witness.CandidateBefore.FrameIndex));
            Assert.Equal(0, sourceAt(witness.CandidateAfter.FrameIndex));
            Assert.True(witness.ReferenceDelta > 0);
            Assert.True(witness.CandidateDelta > 0);
            Assert.True(Math.Abs(witness.CandidateLeadNanoseconds) <= 250_000_000L);
            Assert.True(used.Add(witness.CandidateAfter.FrameIndex));
            Assert.Contains("candidate after", witness.TimingText);
        }
    }

    [Fact]
    public void DoesNotBorrowOrInventTransitionsFromAnotherPhysicalLogger()
    {
        var (a, b, otherLogger, sourceAt) = Fixture();
        var crossLogger = SignalChangeWitnessFinder.Find(a, otherLogger!, sourceAt);
        Assert.Empty(crossLogger.Witnesses);
        Assert.Contains("same verified physical logger", crossLogger.Summary);

        var invalidSources = SignalChangeWitnessFinder.Find(a, b, _ => -1);
        Assert.Empty(invalidSources.Witnesses);
        Assert.Contains("No distinct original-frame transitions", invalidSources.Summary);
    }

    [Fact]
    public void NarrowEventWindowKeepsOnlyRecordedChangesAroundSelectedSourceWitness()
    {
        var (a, b, _, sourceAt) = Fixture();
        var narrowed = SignalChangeWitnessFinder.Find(a, b, sourceAt,
            centerTimestampNanoseconds: 5_000_000_000L,
            windowHalfWidthNanoseconds: 600_000_000L);
        Assert.InRange(narrowed.Witnesses.Count, 1, 3);
        Assert.All(narrowed.Witnesses, witness =>
        {
            Assert.InRange(witness.ReferenceAfter.TimestampNanoseconds,
                4_400_000_000L, 5_600_000_000L);
            Assert.InRange(witness.CandidateAfter.TimestampNanoseconds,
                4_400_000_000L, 5_600_000_000L);
        });
        Assert.True(narrowed.Witnesses.Count < 25);
        Assert.False(narrowed.Truncated);
    }

    [Fact]
    public void ExplicitNoMatchLimitAndCancellationAreNotClaimedAsIndependence()
    {
        var (a, b, _, sourceAt) = Fixture();
        var noMatch = SignalChangeWitnessFinder.Find(a, b, sourceAt,
            maxDistanceNanoseconds: 1);
        Assert.Empty(noMatch.Witnesses);
        Assert.Contains("No distinct original-frame transitions", noMatch.Summary);

        var capped = SignalChangeWitnessFinder.Find(a, b, sourceAt,
            maxWitnesses: 3);
        Assert.Equal(3, capped.Witnesses.Count);
        Assert.True(capped.Truncated);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SignalChangeWitnessFinder.Find(a, b, sourceAt,
                token: cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignalChangeWitnessFinder.Find(a, b, sourceAt,
                maxDistanceNanoseconds: -1));
    }
}
