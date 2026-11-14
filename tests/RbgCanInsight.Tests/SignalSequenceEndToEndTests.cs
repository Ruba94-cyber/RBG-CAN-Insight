using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>
/// Real source-decoder → physical timeline → A/B sequence → bounded frame
/// context pipeline, with known independent oracle frames on two CAN buses.
/// </summary>
public sealed class SignalSequenceEndToEndTests
{
    [Fact]
    public void StrictSequenceAcrossStandardAndExtendedCanKeepsPhysicalFrameProvenance()
    {
        var frames = MixedTrace();
        var timelines = BuildTimelines(frames);
        Assert.Equal(2, timelines.Count);

        var first = timelines.Single(item => item.Signal.Name == "Mode");
        var second = timelines.Single(item => item.Signal.Name == "Load");
        Assert.Equal(0, first.Channel);
        Assert.Equal(1, second.Channel);
        Assert.False(first.Message.IsExtended);
        Assert.True(second.Message.IsExtended);

        var rule = new SignalTriggerSequenceRule(
            new SignalTriggerRule(first.Key, SignalTriggerMode.CrossesAbove, Threshold: 0.5),
            new SignalTriggerRule(second.Key, SignalTriggerMode.CrossesBelow, Threshold: 7),
            SignalTriggerSequenceMode.ThenWithin,
            MinObservedDelayNanoseconds: 50,
            MaxObservedDelayNanoseconds: 110,
            PreContextNanoseconds: 50,
            PostContextNanoseconds: 50);

        var report = SignalTriggerSequenceAnalyzer.Evaluate(timelines, rule);
        var observed = Assert.Single(report.Events);
        Assert.True(report.IsComplete);
        Assert.Equal(2, observed.FirstSourceFrameIndex);
        Assert.Equal(3, observed.SecondSourceFrameIndex);
        Assert.Equal(100, observed.ObservedDelayNanoseconds);
        Assert.Equal(50, observed.ContextStartNanoseconds);
        Assert.Equal(250, observed.ContextEndNanoseconds);

        var context = TraceEventWindowIndex.Locate(frames,
            observed.ContextStartNanoseconds, observed.ContextEndNanoseconds);
        Assert.Equal(new[] { 2, 3 }, context.FrameIndices);
        Assert.All(context.FrameIndices, index =>
            Assert.True(index is 2 or 3));
    }

    [Fact]
    public void ExactSourceKeyCannotBeReboundToHomonymousSignalOnDifferentChannel()
    {
        var frames = MixedTrace();
        var timelines = BuildTimelines(frames);
        var first = timelines.Single(item => item.Signal.Name == "Mode");
        var second = timelines.Single(item => item.Signal.Name == "Load");
        var wrongRule = new SignalTriggerSequenceRule(
            new SignalTriggerRule(first.Key.Replace("ch0", "ch1", StringComparison.Ordinal),
                SignalTriggerMode.CrossesAbove, Threshold: 0.5),
            new SignalTriggerRule(second.Key, SignalTriggerMode.CrossesBelow, Threshold: 7),
            SignalTriggerSequenceMode.ThenWithin, 0, 1000);

        Assert.Throws<ArgumentException>(() =>
            SignalTriggerSequenceAnalyzer.Evaluate(timelines, wrongRule));
    }

    [Fact]
    public void RejectedOrFilteredRawCodeDoesNotFabricateASequence()
    {
        var frames = MixedTrace();
        var original = BuildTimelines(frames);
        var first = original.Single(item => item.Signal.Name == "Mode");
        var second = original.Single(item => item.Signal.Name == "Load");
        var rule = new SignalTriggerSequenceRule(
            new SignalTriggerRule(first.Key, SignalTriggerMode.EntersNamedState,
                NamedState: "Nonexistent"),
            new SignalTriggerRule(second.Key, SignalTriggerMode.CrossesBelow, Threshold: 7),
            SignalTriggerSequenceMode.ThenWithin, 0, 1000);

        var report = SignalTriggerSequenceAnalyzer.Evaluate(original, rule);
        Assert.Empty(report.Events);
        Assert.False(report.IsComplete);
        Assert.NotNull(report.FirstWarning);
    }

    private static CanFrame[] MixedTrace() =>
    [
        new CanFrame(0, 0x100, new byte[] { 0 }, 0, sequence: 1),
        new CanFrame(1, 0x18FEF100, new byte[] { 10 }, 5,
            flags: CanFrameFlags.Extended, sequence: 2),
        new CanFrame(0, 0x100, new byte[] { 1 }, 100, sequence: 3),
        new CanFrame(1, 0x18FEF100, new byte[] { 5 }, 200,
            flags: CanFrameFlags.Extended, sequence: 4)
    ];

    private static IReadOnlyList<SignalTimeline> BuildTimelines(CanFrame[] frames)
    {
        var first = new DbcMessage(0x100, "Status", 8, "ECM", false,
            [new DbcSignal("Mode", 0, 8, DbcByteOrder.Intel, false,
                1, 0, 0, 1, "", "Tester",
                ValueDescriptions: new Dictionary<long, string>
                {
                    [0] = "Off", [1] = "On"
                })]);
        var second = new DbcMessage(0x18FEF100, "LoadBus", 8, "BCM", true,
            [new DbcSignal("Load", 0, 8, DbcByteOrder.Intel, false,
                1, 0, 0, 100, "%", "Tester")]);
        var firstDb = new DbcDatabase("Powertrain",
            new Dictionary<uint, DbcMessage> { [first.Identifier] = first });
        var secondDb = new DbcDatabase("BodyExtended",
            new Dictionary<uint, DbcMessage> { [second.Identifier] = second });
        return SignalTimelineBuilder.Build(frames, [firstDb, secondDb]);
    }
}
