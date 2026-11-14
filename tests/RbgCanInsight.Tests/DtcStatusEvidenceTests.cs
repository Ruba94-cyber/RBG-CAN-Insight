using RbgCanInsight.Diagnostics;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DtcStatusEvidenceTests
{
    private static DtcStatusTransition Event(byte? status, string operation = "ReadDTCInformation") =>
        new(0, status, "", operation, DiagnosticOutcome.Positive, [1, 2], "");

    [Fact]
    public void ExactlyTheChangedStatusBitsAreReported()
    {
        var first = Event(0x01);
        var second = Event(0x0C);
        var text = DtcStatusEvidence.Explain([first, second], second);
        Assert.Contains("0x0C", text);
        Assert.Contains("0x01", text);
        Assert.Contains("set pendingDTC, confirmedDTC", text);
        Assert.Contains("reset testFailed", text);
        Assert.Contains("does not prove DTC erasure", text);
    }

    [Fact]
    public void AcknowledgedClearIsNotPresentedAsEcuStatusReadBack()
    {
        var first = Event(0x09);
        var clear = Event(0, "ClearDiagnosticInformation");
        Assert.Contains("NOT a 0x19 status read back",
            DtcStatusEvidence.Explain([first, clear], clear));
    }

    [Fact]
    public void PostClearReadUsesNewStatusBaseline()
    {
        var first = Event(0x09);
        var clear = Event(0, "ClearDiagnosticInformation");
        var after = Event(0x08);
        var text = DtcStatusEvidence.Explain([first, clear, after], after);
        Assert.Contains("First status observation after", text);
        Assert.DoesNotContain("reset testFailed", text);
    }

    [Fact]
    public void MissingOrStaleStatusDoesNotInferFaultState()
    {
        var unknown = Event(null);
        Assert.Contains("No recorded status byte",
            DtcStatusEvidence.Explain([unknown], unknown));
        Assert.Contains("no longer in", DtcStatusEvidence.Explain([], unknown));
    }
}
