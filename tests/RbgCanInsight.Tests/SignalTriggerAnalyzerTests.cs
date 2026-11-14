using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalTriggerAnalyzerTests
{
    [Fact]
    public void CrossingAboveUsesTwoObservedFramesAndStrictThreshold()
    {
        var timeline = Samples([9, 10, 11, 12], [0, 100, 200, 300]);
        var result = SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.CrossesAbove, Threshold: 10));
        var triggered = Assert.Single(result.Events);
        Assert.Equal(1, triggered.PreviousFrameIndex);
        Assert.Equal(2, triggered.CurrentFrameIndex);
        Assert.Equal(10, triggered.PreviousPhysicalValue);
        Assert.Equal(11, triggered.CurrentPhysicalValue);
        Assert.Equal(100, triggered.UncertaintyNanoseconds);
        Assert.Equal(1, result.MatchedEvents);
    }

    [Fact]
    public void CrossingBelowRequiresPreviousAtOrAboveThreshold()
    {
        var timeline = Samples([12, 10, 9, 8, 12], [0, 1, 2, 3, 4]);
        var result = SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.CrossesBelow, Threshold: 10));
        var eventRow = Assert.Single(result.Events);
        Assert.Equal(2, eventRow.CurrentFrameIndex);
    }

    [Fact]
    public void ChangeToleranceUsesSourcePhysicalValuesAndExcludesEquality()
    {
        var timeline = Samples([1, 2, 2, 5], [0, 1, 2, 3]);
        var result = SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.ChangesByMoreThan,
                ChangeTolerance: 1));
        Assert.Equal(1, result.MatchedEvents);
        Assert.Equal(3, Assert.Single(result.Events).CurrentFrameIndex);
    }

    [Fact]
    public void NamedStateTriggersRequireTwoKnownDeclaredStates()
    {
        var timeline = Samples([0, 1, 1, 2, 1, 0], [0, 10, 20, 30, 40, 50]);
        var enter = SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.EntersNamedState,
                NamedState: "Running"));
        // 0→1 is a supported transition. Unknown raw 2→1 is deliberately
        // not interpreted as entering from a declared state.
        Assert.Single(enter.Events);
        Assert.Equal(1, enter.Events[0].CurrentFrameIndex);
        Assert.Equal("Stopped", enter.Events[0].PreviousNamedState);
        Assert.Equal("Running", enter.Events[0].CurrentNamedState);

        var leave = SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.LeavesNamedState,
                NamedState: "Running"));
        Assert.Single(leave.Events);
        Assert.Equal(5, leave.Events[0].CurrentFrameIndex);
    }

    [Fact]
    public void NamedStateIsValidatedAgainstSourceDeclarations()
    {
        var timeline = Samples([0, 1], [0, 10]);
        var missing = SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.EntersNamedState, NamedState: "NotDeclared"));
        Assert.Empty(missing.Events);
        Assert.Contains("not declared", missing.Warning!);
        Assert.Throws<ArgumentException>(() => SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.EntersNamedState)));
    }

    [Fact]
    public void EventSeparationAndOutputLimitHaveSeparateAuditableCounts()
    {
        var timeline = Samples([0, 2, 0, 2, 0, 2, 0, 2],
            [0, 10, 20, 30, 40, 50, 60, 70]);
        var result = SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.CrossesAbove, Threshold: 1,
                MinimumEventSeparationNanoseconds: 30), maxEvents: 1);
        Assert.Single(result.Events);
        Assert.Equal(2, result.MatchedEvents); // at 10 and 50
        Assert.Equal(2, result.SuppressedBySeparation); // at 30 and 70
        Assert.Equal(1, result.OmittedEvents);
        Assert.Equal(1, result.Events[0].CurrentFrameIndex);
    }

    [Fact]
    public void NoMatchesAndZeroLimitDoNotProduceFalseEvidence()
    {
        var timeline = Samples([0, 0, 0], [0, 10, 20]);
        var unchanged = SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.ChangesByMoreThan));
        Assert.Empty(unchanged.Events);

        var crossing = Samples([0, 2, 0, 2], [0, 10, 20, 30]);
        var bounded = SignalTriggerAnalyzer.Evaluate(crossing,
            new(crossing.Key, SignalTriggerMode.CrossesAbove, Threshold: 1),
            maxEvents: 0);
        Assert.Empty(bounded.Events);
        Assert.Equal(2, bounded.MatchedEvents);
        Assert.Equal(2, bounded.OmittedEvents);
    }

    [Fact]
    public void CannotApplyRuleForSameNamedSignalOnWrongPhysicalChannel()
    {
        var busOne = Samples([0, 1], [0, 1], channel: 0);
        var busTwo = Samples([0, 1], [0, 1], channel: 1);
        Assert.NotEqual(busOne.Key, busTwo.Key);
        Assert.Throws<ArgumentException>(() => SignalTriggerAnalyzer.Evaluate(busTwo,
            new(busOne.Key, SignalTriggerMode.CrossesAbove, Threshold: 0)));
    }

    [Theory]
    [InlineData(double.NaN, 0d)]
    [InlineData(1d, -1d)]
    public void NonfiniteThresholdOrNegativeToleranceAreRejected(double threshold, double tolerance)
    {
        var timeline = Samples([0, 1], [0, 10]);
        Assert.Throws<ArgumentException>(() => SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.CrossesAbove,
                Threshold: threshold, ChangeTolerance: tolerance)));
    }

    [Fact]
    public void InvalidOutputLimitAndCanceledRequestDoNotReturnPartialAnalysis()
    {
        var timeline = Samples([0, 1], [0, 10]);
        Assert.Throws<ArgumentOutOfRangeException>(() => SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.CrossesAbove), maxEvents: -1));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.CrossesAbove), cancellationToken: cancel.Token));
    }

    [Fact]
    public void NamedStateCollisionIsConservativelyRejected()
    {
        var timeline = Samples([0, 1, 0], [0, 10, 20], factor: 0);
        var report = SignalTriggerAnalyzer.Evaluate(timeline,
            new(timeline.Key, SignalTriggerMode.EntersNamedState, NamedState: "Running"));
        Assert.Empty(report.Events);
        Assert.NotNull(report.Warning);
    }

    private static SignalTimeline Samples(byte[] values, long[] times,
        int channel = 0, double factor = 1)
    {
        Assert.Equal(values.Length, times.Length);
        var signal = new DbcSignal("Mode", 0, 8, DbcByteOrder.Intel,
            false, factor, 0, 0, 255, "", "ECU",
            ValueDescriptions: new Dictionary<long, string>
            {
                [0] = "Stopped", [1] = "Running"
            });
        var frame = new DbcMessage(0x123, "Status", 8, "ECU", false, [signal]);
        var database = new DbcDatabase("sample",
            new Dictionary<uint, DbcMessage> { [0x123] = frame });
        var frames = values.Select((value, index) => new CanFrame(
            channel, 0x123, new byte[] { value }, times[index],
            sequence: index + 1L)).ToArray();
        return Assert.Single(SignalTimelineBuilder.Build(frames, [database]));
    }
}
