using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Independent temporal-oracle fixtures using known source-frame
/// observations; no ECU event instant is inferred between samples.</summary>
public sealed class SignalTriggerSequenceAnalyzerTests
{
    private const string FirstKey = "A|ch0|00000123|S|Status.Mode";
    private const string SecondKey = "B|ch1|00000345|E|Power.Load";
    private static SignalTriggerRule FirstRule => new(FirstKey,
        SignalTriggerMode.CrossesAbove, Threshold: 10);
    private static SignalTriggerRule SecondRule => new(SecondKey,
        SignalTriggerMode.CrossesBelow, Threshold: 20);

    private static SignalTriggerEvent Event(string key, long time, int frame) =>
        new(key, time - 10, time, frame - 1, frame, 0, 1, null, null);
    private static SignalTriggerReport Report(
        SignalTriggerRule rule, params (long Time, int Frame)[] values) =>
        new(rule.SignalKey, rule.Mode, values.Select(value =>
            Event(rule.SignalKey, value.Time, value.Frame)).ToArray(),
            values.Length, 0, 0, null);
    private static SignalTriggerSequenceRule Rule(
        SignalTriggerSequenceMode mode = SignalTriggerSequenceMode.ThenWithin,
        long min = 0, long max = 100, long before = 0, long after = 0) =>
        new(FirstRule, SecondRule, mode, min, max, before, after);

    [Fact]
    public void ThenWithinRetainsBothOriginalEventsAndBoundedContext()
    {
        var result = SignalTriggerSequenceAnalyzer.Pair(
            Rule(min: 20, max: 100, before: 25, after: 75),
            Report(FirstRule, (100, 3)), Report(SecondRule, (200, 9)));
        var pair = Assert.Single(result.Events);
        Assert.True(result.IsComplete);
        Assert.Equal(100, pair.ObservedDelayNanoseconds);
        Assert.Equal(3, pair.FirstSourceFrameIndex);
        Assert.Equal(9, pair.SecondSourceFrameIndex);
        Assert.Equal(75, pair.ContextStartNanoseconds);
        Assert.Equal(275, pair.ContextEndNanoseconds);
        Assert.Contains("unknown", pair.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdenticalTimestampCannotProveAThenB()
    {
        var result = SignalTriggerSequenceAnalyzer.Pair(Rule(),
            Report(FirstRule, (100, 4)), Report(SecondRule, (100, 5)));
        Assert.Empty(result.Events);
        Assert.Equal(0, result.MatchedPairs);
    }

    [Fact]
    public void SymmetricBothWithinCanMatchObservedBJustBeforeA()
    {
        var result = SignalTriggerSequenceAnalyzer.Pair(
            Rule(mode: SignalTriggerSequenceMode.BothWithin, max: 15),
            Report(FirstRule, (100, 4)), Report(SecondRule, (90, 2)));
        var pair = Assert.Single(result.Events);
        Assert.Equal(-10, pair.ObservedDelayNanoseconds);
        Assert.Equal(2, pair.SecondSourceFrameIndex);
    }

    [Fact]
    public void MinimumAndMaximumDelaysAreInclusiveButRealOrderMustBeStrict()
    {
        var result = SignalTriggerSequenceAnalyzer.Pair(Rule(min: 10, max: 20),
            Report(FirstRule, (100, 2), (200, 4), (300, 6)),
            Report(SecondRule, (109, 3), (210, 5), (320, 7)));
        Assert.Equal(2, result.MatchedPairs);
        Assert.Collection(result.Events,
            first => Assert.Equal(10, first.ObservedDelayNanoseconds),
            second => Assert.Equal(20, second.ObservedDelayNanoseconds));
    }

    [Fact]
    public void OneSecondaryEventCannotBeClaimedByMultiplePrimaryEvents()
    {
        var result = SignalTriggerSequenceAnalyzer.Pair(Rule(max: 100),
            Report(FirstRule, (100, 1), (150, 2)), Report(SecondRule, (160, 3)));
        Assert.Single(result.Events);
        Assert.Equal(1, result.MatchedPairs);
    }

    [Fact]
    public void DifferentSourceSignalsDoNotGetSilentlySwapped()
    {
        var wrong = new SignalTriggerReport(SecondKey, FirstRule.Mode, [], 0, 0, 0, null);
        Assert.Throws<ArgumentException>(() => SignalTriggerSequenceAnalyzer.Pair(
            Rule(), wrong, Report(SecondRule)));
    }

    [Fact]
    public void PerInputAndOutputLimitsAreDisclosedWithoutClaimingCompleteness()
    {
        var first = Report(FirstRule, (100, 3), (200, 5)) with
        {
            MatchedEvents = 20,
            OmittedEvents = 18
        };
        var second = Report(SecondRule, (110, 4), (210, 6));
        var result = SignalTriggerSequenceAnalyzer.Pair(Rule(max: 30), first,
            second, maxPairs: 1);
        Assert.False(result.IsComplete);
        Assert.Equal(2, result.MatchedPairs);
        Assert.Single(result.Events);
        Assert.Equal(1, result.OmittedPairs);
        Assert.Equal(18, result.FirstInputOmitted);
        Assert.Contains("partial", result.Summary);
    }

    [Fact]
    public void UnsupportedSourceWarningForbidsClaimOfCompleteEvidence()
    {
        var first = Report(FirstRule) with
        {
            Warning = "Source conversion unsupported: raw-only."
        };
        var result = SignalTriggerSequenceAnalyzer.Pair(Rule(), first,
            Report(SecondRule));
        Assert.False(result.IsComplete);
        Assert.Empty(result.Events);
        Assert.Equal("Source conversion unsupported: raw-only.", result.FirstWarning);
    }

    [Fact]
    public void SameSignalCooccurrenceSkipsSelfFrameAndChecksNextIndependentEvent()
    {
        var sharedBRule = new SignalTriggerRule(FirstKey,
            SignalTriggerMode.CrossesBelow, Threshold: 10);
        var rule = new SignalTriggerSequenceRule(FirstRule, sharedBRule,
            SignalTriggerSequenceMode.BothWithin, 0, 50);
        var a = Report(FirstRule, (100, 1));
        var b = Report(sharedBRule, (100, 1), (130, 3));
        var report = SignalTriggerSequenceAnalyzer.Pair(rule, a, b);
        var pair = Assert.Single(report.Events);
        Assert.Equal(1, pair.FirstSourceFrameIndex);
        Assert.Equal(3, pair.SecondSourceFrameIndex);
        Assert.Equal(30, pair.ObservedDelayNanoseconds);
    }

    [Fact]
    public void InvalidWindowOrContextNeverReturnsFabricatedPairs()
    {
        Assert.Throws<ArgumentException>(() =>
            SignalTriggerSequenceAnalyzer.Pair(Rule(min: 100, max: 99),
                Report(FirstRule), Report(SecondRule)));
        Assert.Throws<ArgumentException>(() =>
            SignalTriggerSequenceAnalyzer.Pair(Rule(
                mode: SignalTriggerSequenceMode.BothWithin, min: 1),
                Report(FirstRule), Report(SecondRule)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignalTriggerSequenceAnalyzer.Pair(Rule(),
                Report(FirstRule), Report(SecondRule), maxPairs: -1));
    }

    [Fact]
    public void CanceledRequestReturnsNoPartialAnalysis()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SignalTriggerSequenceAnalyzer.Pair(Rule(),
                Report(FirstRule), Report(SecondRule),
                cancellationToken: source.Token));
    }

    [Fact]
    public void ContextBoundsSaturateWithoutTimestampOverflow()
    {
        var a = long.MaxValue - 150;
        var b = long.MaxValue - 50;
        var result = SignalTriggerSequenceAnalyzer.Pair(
            Rule(max: 100, before: 1000, after: 1000),
            Report(FirstRule, (a, 1)), Report(SecondRule, (b, 2)));
        var item = Assert.Single(result.Events);
        Assert.Equal(long.MaxValue, item.ContextEndNanoseconds);
        Assert.Equal(a - 1000, item.ContextStartNanoseconds);
    }
}
