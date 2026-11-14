using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class FaultEventContextAnalyzerTests
{
    private static (IReadOnlyList<SignalTimeline> Curves, FaultMemoryEntry Fault,
        DtcStatusTransition Event, Func<int,int> SourceAt) Fixture()
    {
        var dbc = new DbcParser().Parse("engine", new StringReader(
            """
            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|8@1+ (1,0) [0|255] "rpm" ECM
             SG_ Torque : 8|8@1+ (1,0) [0|255] "Nm" ECM
             SG_ Stationary : 16|8@1+ (1,0) [0|255] "" ECM
            """));
        var frames = new[]
        {
            new CanFrame(0, 256, [10, 20, 1], 1_000_000_000L), // Logger 1, frame #1
            new CanFrame(0, 256, [90, 90, 1], 1_200_000_000L), // Logger 2, frame #2
            new CanFrame(0, 256, [12, 24, 1], 2_000_000_000L), // Logger 1, frame #3
            new CanFrame(0, 256, [91, 99, 1], 2_150_000_000L), // Logger 2, frame #4
            new CanFrame(0, 256, [12, 24, 1], 3_000_000_000L)  // Logger 1, frame #5
        };
        int SourceAt(int index) => index >= 0 && index < frames.Length ?
            index is 1 or 3 ? 1 : 0 : -1;
        var curves = SignalTimelineBuilder.Build(frames, [dbc],
            sourceAt: SourceAt, sourcePaths: ["first.asc","second.asc"]);
        var observed = new DtcStatusTransition(2_100_000_000L, 0x09,
            "testFailed, confirmedDTC", "ReadDTCInformation",
            DiagnosticOutcome.Positive, [2], "", []);
        var fault = new FaultMemoryEntry(0x123456, "123456", "Engine RPM plausibility",
            "", "ECM", 2_100_000_000L, 2_100_000_000L, 1,
            0x09, "testFailed", false, null, [observed])
        {
            OriginalSourceIndex = 0, SourceChannel = 0
        };
        return (curves, fault, observed, SourceAt);
    }

    [Fact]
    public void ReportsOriginalFramesOfGenuineChangesNearRecordedDtcObservation()
    {
        var (curves, fault, selected, at) = Fixture();
        var report = FaultEventContextAnalyzer.Build(fault, selected, curves, at,
            beforeNanoseconds: 400_000_000L, afterNanoseconds: 200_000_000L);
        Assert.Equal(1_700_000_000L, report.WindowStartNanoseconds);
        Assert.Equal(2_300_000_000L, report.WindowEndNanoseconds);
        Assert.Equal(2, report.NearbyChanges.Count);
        // Recorded proximity is identical; explicit DTC/DBC label overlap
        // should put EngineSpeed before unrelated torque, never claim cause.
        Assert.Equal("EngineSpeed", report.NearbyChanges[0].Timeline.Signal.Name);
        Assert.True(report.NearbyChanges[0].CatalogNameHintScore >
            report.NearbyChanges[1].CatalogNameHintScore);
        Assert.Contains("NOT a proven fault dependency", report.NearbyChanges[0].CatalogNameHint);
        Assert.All(report.NearbyChanges, item =>
        {
            Assert.Equal(0, item.Timeline.OriginalSourceIndex);
            Assert.Equal(0, item.Before.FrameIndex);
            Assert.Equal(2, item.After.FrameIndex);
            Assert.Equal(-100_000_000L, item.OffsetNanoseconds);
            Assert.NotEqual(item.Before.Value, item.After.Value);
        });
        Assert.DoesNotContain(report.NearbyChanges, item => item.Timeline.Signal.Name == "Stationary");
        Assert.Contains("not necessarily the physical fault onset", report.EventMeaning);
        Assert.Contains("0x09", report.StatusEvidence);
    }

    [Fact]
    public void DoesNotBorrowEvidenceFromOtherLoggerOrFutureOutsideWindow()
    {
        var (curves, fault, selected, at) = Fixture();
        var report = FaultEventContextAnalyzer.Build(fault, selected, curves, at,
            beforeNanoseconds: 40_000_000L, afterNanoseconds: 40_000_000L);
        Assert.Empty(report.NearbyChanges);
    }

    [Fact]
    public void RejectsCrossLoggerOriginalEventAndUnknownFrame()
    {
        var (curves, fault, selected, at) = Fixture();
        var wrong = selected with { FrameIndices = [1] };
        var alteredFault = fault with { Timeline = [wrong] };
        Assert.Throws<InvalidOperationException>(() =>
            FaultEventContextAnalyzer.Build(alteredFault, wrong, curves, at));
        var outOfRange = selected with { FrameIndices = [999] };
        Assert.Throws<InvalidOperationException>(() =>
            FaultEventContextAnalyzer.Build(fault with { Timeline = [outOfRange] },
                outOfRange, curves, at));
        Assert.Throws<InvalidOperationException>(() =>
            FaultEventContextAnalyzer.Build(fault, selected with { Status = 0 }, curves, at));
    }

    [Fact]
    public void AcknowledgedClearIsNotMistakenForAnActualFaultDeactivation()
    {
        var (curves, fault, _, at) = Fixture();
        var clear = new DtcStatusTransition(2_100_000_000L, null, "",
            "ClearDiagnosticInformation", DiagnosticOutcome.Positive, [2], "", []);
        var report = FaultEventContextAnalyzer.Build(fault with { Timeline = [clear] },
            clear, curves, at);
        Assert.True(report.IsClearOperation);
        Assert.Contains("not a verified DTC deactivation", report.EventMeaning);
        Assert.Contains("NOT a 0x19 status read back", report.StatusEvidence);
    }

    [Fact]
    public void UnknownSourceOrCanceledScanFailsClosed()
    {
        var (curves, fault, selected, at) = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            FaultEventContextAnalyzer.Build(fault with { OriginalSourceIndex = -1 },
                selected, curves, at));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FaultEventContextAnalyzer.Build(fault, selected, curves, at, cancellationToken: cts.Token));
    }
}
