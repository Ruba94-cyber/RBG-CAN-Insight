using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticSessionStateTrackerTests
{
    private static CanFrame Frame(uint id, byte[] data, long ms) =>
        new(0, id, data.Concat(new byte[8 - data.Length]).ToArray(), ms * 1_000_000);

    private static IReadOnlyList<DiagnosticTransaction> Transactions(params CanFrame[] frames) =>
        UdsTransactionAnalyzer.Analyze(frames);

    [Fact]
    public void StateIsUnknownUntilAPositiveResponseIsRecorded()
    {
        var tx = Transactions(
            Frame(0x7E0, [0x02, 0x10, 0x03], 0),
            Frame(0x7E8, [0x03, 0x7F, 0x10, 0x22], 10));   // refused: no state change
        var tracker = DiagnosticSessionStateTracker.Reconstruct(tx);
        Assert.Empty(tracker.Changes);
        var ecu = tx[0].Endpoint.Ecu;
        Assert.False(tracker.StateAt(ecu, 1_000_000_000).Known);
    }

    [Fact]
    public void SessionSecurityAndResetAreReconstructedInOrder()
    {
        var tx = Transactions(
            Frame(0x7E0, [0x02, 0x10, 0x03], 0), Frame(0x7E8, [0x06, 0x50, 0x03, 0, 0x32, 1, 0xF4], 10),
            Frame(0x7E0, [0x04, 0x27, 0x02, 0xAA, 0xBB], 100), Frame(0x7E8, [0x02, 0x67, 0x02], 110),
            Frame(0x7E0, [0x02, 0x11, 0x01], 500), Frame(0x7E8, [0x02, 0x51, 0x01], 510));
        var tracker = DiagnosticSessionStateTracker.Reconstruct(tx);
        var ecu = tx[0].Endpoint.Ecu;

        var afterSession = tracker.StateAt(ecu, 50_000_000);
        Assert.Equal(0x03, afterSession.Session);
        Assert.Equal(0, afterSession.SecurityLevel);          // observed locked

        var afterUnlock = tracker.StateAt(ecu, 200_000_000);
        Assert.Equal(0x01, afterUnlock.SecurityLevel);         // sendKey 0x02 unlocks level 0x01

        var afterReset = tracker.StateAt(ecu, 600_000_000);
        Assert.Equal(0x01, afterReset.Session);                // default session after reset
        Assert.Equal(0, afterReset.SecurityLevel);
        Assert.All(tracker.Changes, change => Assert.NotEmpty(change.FrameIndices));
    }

    [Fact]
    public void RoutineRequestsAreCheckedAgainstRecordedSessionAndSecurity()
    {
        var routine = new DiagnosticRoutine(0x0203, "Calibrate", "", "", "ECU", "c.odx",
            RequiredSession: "Extended", RequiredSecurityLevel: "0x01");
        var catalog = new DidCatalog("c", "ECU", "c.odx", [], Routines: [routine]);
        var frames = new[]
        {
            // Routine BEFORE any state is observed -> unverifiable
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x02, 0x03], 0), Frame(0x7E8, [0x04, 0x71, 0x01, 0x02, 0x03], 10),
            // Extended session but still locked -> not met (security)
            Frame(0x7E0, [0x02, 0x10, 0x03], 100), Frame(0x7E8, [0x06, 0x50, 0x03, 0, 0x32, 1, 0xF4], 110),
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x02, 0x03], 200), Frame(0x7E8, [0x04, 0x71, 0x01, 0x02, 0x03], 210),
            // Unlock level 1, then routine -> satisfied (no finding)
            Frame(0x7E0, [0x04, 0x27, 0x02, 0xAA, 0xBB], 300), Frame(0x7E8, [0x02, 0x67, 0x02], 310),
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x02, 0x03], 400), Frame(0x7E8, [0x04, 0x71, 0x01, 0x02, 0x03], 410)
        };
        var transactions = UdsTransactionAnalyzer.Analyze(frames, [catalog]);

        var report = DiagnosticSpecTraceChecker.Check(transactions, [catalog]);

        var unverifiable = Assert.Single(report.Findings,
            f => f.Kind == SpecTraceFindingKind.RoutinePrerequisiteUnverifiable);
        Assert.Equal([0, 1], unverifiable.FrameIndices);
        var notMet = Assert.Single(report.Findings,
            f => f.Kind == SpecTraceFindingKind.RoutinePrerequisiteNotMet);
        Assert.Contains("recorded locked", notMet.Identifier);
        Assert.Equal([4, 5], notMet.FrameIndices);
    }

    [Theory]
    [InlineData("Extended", 3)]
    [InlineData("default", 1)]
    [InlineData("Programming session", 2)]
    [InlineData("0x40", 0x40)]
    [InlineData("3", 3)]
    [InlineData("vendor specific text", null)]
    [InlineData("", null)]
    public void DeclaredSessionTextIsParsedConservatively(string text, int? expected) =>
        Assert.Equal(expected, DiagnosticSpecTraceChecker.ParseSession(text));
}
