using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class NearbySignalChangeFinderTests
{
    private static (IReadOnlyList<SignalTimeline> Curves, SignalTimeline Focus) Fixture()
    {
        var db = new DbcParser().Parse("engine", new StringReader(
            """
            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|8@1+ (1,0) [0|255] "rpm" ECM
             SG_ Torque : 8|8@1+ (1,0) [0|255] "Nm" ECM
             SG_ Stationary : 16|8@1+ (1,0) [0|255] "" ECM
            """));
        // Two independent physical loggers have identical CAN IDs/values but
        // no independently verified synchronization. Never mix them.
        var frames = new[]
        {
            new CanFrame(0, 256, [10, 20, 1], 1_000_000_000L), // Logger 1
            new CanFrame(0, 256, [88, 88, 1], 1_200_000_000L), // Logger 2
            new CanFrame(0, 256, [12, 25, 1], 2_000_000_000L), // Logger 1
            new CanFrame(0, 256, [89, 89, 1], 2_200_000_000L), // Logger 2
            new CanFrame(0, 256, [12, 25, 1], 3_000_000_000L), // Logger 1
        };
        var curves = SignalTimelineBuilder.Build(frames, [db],
            sourceAt: i => i is 1 or 3 ? 1 : 0,
            sourcePaths: ["first.asc", "second.asc"]);
        return (curves,
            Assert.Single(curves.Where(t => t.Signal.Name == "EngineSpeed" &&
                t.OriginalSourceIndex == 0)));
    }

    [Fact]
    public void FindsActualNearestTransitionAndKeepsSourceFramesAndOtherLoggerSeparate()
    {
        var (curves, focus) = Fixture();
        var matches = NearbySignalChangeFinder.Find(curves, focus,
            2_100_000_000L, 400_000_000L);
        var eventRow = Assert.Single(matches);
        Assert.Equal("Torque", eventRow.Timeline.Signal.Name);
        Assert.Equal(0, eventRow.Timeline.OriginalSourceIndex);
        Assert.Equal(0, eventRow.Before.FrameIndex);
        Assert.Equal(2, eventRow.After.FrameIndex);
        Assert.Equal(20d, eventRow.Before.Value);
        Assert.Equal(25d, eventRow.After.Value);
        Assert.Equal(-100_000_000L, eventRow.OffsetNanoseconds);
        Assert.DoesNotContain(matches, row => row.Timeline.Signal.Name == "Stationary");
    }

    [Fact]
    public void NoTransitionsOutsideWindowAndNoFalseChangesForConstantSignals()
    {
        var (curves, focus) = Fixture();
        Assert.Empty(NearbySignalChangeFinder.Find(curves, focus,
            9_000_000_000L, 100_000_000L));
    }

    [Fact]
    public void RequiresValidBoundsAndCanBeCanceled()
    {
        var (curves, focus) = Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NearbySignalChangeFinder.Find(curves, focus, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NearbySignalChangeFinder.Find(curves, focus, 1, halfWindowNanoseconds: 0));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            NearbySignalChangeFinder.Find(curves, focus, 2_000_000_000L,
                cancellationToken: cts.Token));
    }
}
