using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalStateTransitionAnalyzerTests
{
    [Fact]
    public void NamedStateTransitionsRetainSourceFrameAndUncertaintyWithoutInventingSwitchTime()
    {
        var report = Analyze([0, 0, 1, 1, 0], [0, 100, 150, 250, 300]);
        Assert.Equal(2, report.TotalTransitions);
        Assert.Equal(0, report.OmittedTransitions);
        Assert.Collection(report.Transitions,
            first =>
            {
                Assert.Equal("Idle", first.PreviousDeclaredState);
                Assert.Equal("Drive", first.CurrentDeclaredState);
                Assert.Equal(1, first.PreviousSourceFrameIndex);
                Assert.Equal(2, first.CurrentSourceFrameIndex);
                Assert.Equal(100, first.PreviousStateObservedSpanNanoseconds);
                Assert.Equal(50, first.TransitionTimeUncertaintyNanoseconds);
            },
            second =>
            {
                Assert.Equal("Drive", second.PreviousDeclaredState);
                Assert.Equal("Idle", second.CurrentDeclaredState);
                Assert.Equal(3, second.PreviousSourceFrameIndex);
                Assert.Equal(4, second.CurrentSourceFrameIndex);
                Assert.Equal(100, second.PreviousStateObservedSpanNanoseconds);
                Assert.Equal(50, second.TransitionTimeUncertaintyNanoseconds);
            });
    }

    [Fact]
    public void UnnamedInterveningRawCodeBreaksContinuity()
    {
        var report = Analyze([0, 2, 1, 1, 0], [0, 100, 150, 250, 300]);
        var item = Assert.Single(report.Transitions);
        Assert.Equal("Drive", item.PreviousDeclaredState);
        Assert.Equal("Idle", item.CurrentDeclaredState);
    }

    [Fact]
    public void UnchangedStatesDoNotCreateFakeTransitions()
    {
        var report = Analyze([0, 0, 0], [0, 10, 20]);
        Assert.Empty(report.Transitions);
    }

    [Fact]
    public void BoundedResultReportsEveryOmittedChangeExplicitly()
    {
        var report = Analyze([0, 1, 0, 1, 0], [0, 10, 20, 30, 40], max: 1);
        Assert.Equal(4, report.TotalTransitions);
        Assert.Single(report.Transitions);
        Assert.Equal(3, report.OmittedTransitions);
    }

    [Fact]
    public void ZeroExportLimitStillCountsActualObservedTransitions()
    {
        var report = Analyze([0, 1, 0], [0, 10, 20], max: 0);
        Assert.Empty(report.Transitions);
        Assert.Equal(2, report.OmittedTransitions);
    }

    [Fact]
    public void ConflictingPhysicalValuesDoNotCreateNamedTransitions()
    {
        // Both symbolic codes convert to the identical physical value.
        var report = Analyze([0, 1, 0], [0, 10, 20], factor: 0);
        Assert.Empty(report.Transitions);
        Assert.Equal(1, report.UnsupportedOrAmbiguousSignals);
    }

    [Fact]
    public void InvalidExportLimitIsExplicitlyRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Analyze([0, 1], [0, 10], max: -1));
    }

    [Fact]
    public void CanceledTransitionAnalysisStopsBeforeProcessingNewTimelines()
    {
        var signal = new DbcSignal("Mode", 0, 8, DbcByteOrder.Intel,
            false, 1, 0, 0, 1, "", "ECM",
            ValueDescriptions: new Dictionary<long, string> { [0] = "Off", [1] = "On" });
        var message = new DbcMessage(0x100, "Engine", 8, "ECM", false, [signal]);
        var timeline = new SignalTimeline(message, signal, "fixture", channel: 0);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SignalStateTransitionAnalyzer.Analyze([timeline],
                cancellationToken: canceled.Token));
    }

    private static SignalStateTransitionReport Analyze(
        byte[] rawValues, long[] timestamps, int max = 25000, double factor = 1)
    {
        Assert.Equal(rawValues.Length, timestamps.Length);
        var signal = new DbcSignal("Mode", 0, 8, DbcByteOrder.Intel,
            false, factor, 0, 0, 2, "", "ECM",
            ValueDescriptions: new Dictionary<long, string>
            {
                [0] = "Idle", [1] = "Drive"
            });
        var message = new DbcMessage(0x100, "Status", 8, "ECM", false, [signal]);
        var db = new DbcDatabase("Synthetic", new Dictionary<uint, DbcMessage>
        {
            [0x100] = message
        });
        var frames = rawValues.Select((value, index) =>
            new CanFrame(0, 0x100, new byte[] { value }, timestamps[index],
                sequence: index + 1L)).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [db]);
        return SignalStateTransitionAnalyzer.Analyze(timelines, max);
    }
}
