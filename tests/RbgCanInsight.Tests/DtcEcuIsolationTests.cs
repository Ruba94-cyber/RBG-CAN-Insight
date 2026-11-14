using RbgCanInsight.Diagnostics;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DtcEcuIsolationTests
{
    private static DiagnosticTransaction Transaction(
        string ecu, byte service, long timestamp, DecodedDtc? dtc = null,
        int clearGroup = 0xFFFFFF)
    {
        var decoded = new DecodedUdsOperation(
            service, service == 0x14 ? "ClearDiagnosticInformation" : "ReadDTCInformation",
            "", "", "", [], dtc is null ? [] : [dtc], null, "");
        var clearRequest = service == 0x14
            ? new IsoTpMessage(0, 0x7E0, false, timestamp, timestamp, [0],
                [0x14, (byte)(clearGroup >> 16), (byte)(clearGroup >> 8), (byte)clearGroup],
                true, string.Empty)
            : null;
        return new DiagnosticTransaction(
            (int)timestamp, clearRequest, [], new DiagnosticEndpoint(
                "Tester", ecu, DiagnosticDirection.EcuToTester,
                DiagnosticEvidence.Confirmed, "physical"),
            DiagnosticOutcome.Positive, decoded, timestamp, timestamp,
            null, [], "");
    }



    [Fact]
    public void FaultMemoryCsvPreservesSameDtcFromSeparateOriginalLoggers()
    {
        var dtc = new DecodedDtc(0x123456, "0x123456", "Sensor", 0x08, "confirmed", "");
        var first = Transaction("ECM", 0x19, 1, dtc) with { FrameIndices = [0] };
        var second = Transaction("ECM", 0x19, 1, dtc) with { FrameIndices = [1] };
        var faults = DiagnosticInsightAnalyzer.Analyze([first, second], [],
            sourceAt: index => index).Faults;
        var rows = FaultMemoryTable.Build(faults);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { 0, 1 }, rows.Select(row => row.OriginalSourceIndex).ToArray());
        var csv = FaultMemoryTable.ToCsv(rows);
        Assert.StartsWith("ECU,DTC,Name,Status,Status text", csv);
        Assert.Contains("Original logger,CAN bus,CDD/ODX definition", csv);
        Assert.Contains("Logger 1,CAN channel unknown", csv);
        Assert.Contains("Logger 2,CAN channel unknown", csv);
    }

    [Fact]
    public void TwoLoggersSameDtcSameEcuRemainIndependentWhenOneLoggerClearsIt()
    {
        var fault = new DecodedDtc(0x123456, "123456", "Sensor", 0x08, "confirmed", "");
        DiagnosticTransaction Recorded(string ecu, byte service, long timestamp,
            int frame, DecodedDtc? dtc = null)
        {
            var transaction = Transaction(ecu, service, timestamp, dtc);
            return transaction with
            {
                FrameIndices = [frame],
                // Fault memory is isolated per CAN bus (e5c756e). A read with no
                // bus evidence (-1) can never be cleared by a request seen on
                // bus 0, so the fixture must record the read request's channel.
                Request = (transaction.Request ?? new IsoTpMessage(0, 0x7E0, false,
                        timestamp, timestamp, [frame], [service, 0x02, 0xFF], true, string.Empty))
                    with { FrameIndices = [frame] }
            };
        }
        var transactions = new[]
        {
            Recorded("ECM", 0x19, 1, 0, fault),
            Recorded("ECM", 0x19, 1, 1, fault),
            Recorded("ECM", 0x14, 2, 2)
        };
        int SourceAt(int frame) => frame == 1 ? 1 : 0;
        var observed = DiagnosticInsightAnalyzer.Analyze(transactions, [], sourceAt: SourceAt).Faults;
        Assert.Equal(2, observed.Count);
        var cleared = Assert.Single(observed, row => row.OriginalSourceIndex == 0);
        var retained = Assert.Single(observed, row => row.OriginalSourceIndex == 1);
        Assert.True(cleared.WasCleared);
        Assert.Equal("cleared", cleared.CurrentStatusText);
        Assert.False(retained.WasCleared);
        Assert.Equal((byte)0x08, retained.CurrentStatus);
        Assert.All(cleared.Timeline.SelectMany(t => t.FrameIndices),
            index => Assert.Equal(0, SourceAt(index)));
        Assert.All(retained.Timeline.SelectMany(t => t.FrameIndices),
            index => Assert.Equal(1, SourceAt(index)));
    }

    [Fact]
    public void AForeignLoggerInsideDtcTransactionCannotBecomeAValidFaultObservation()
    {
        var fault = new DecodedDtc(0x111111, "111111", "Fault", 0x01, "status", "");
        var recorded = Transaction("ECM", 0x19, 1, fault) with
        {
            FrameIndices = [0, 1]
        };
        Assert.Throws<InvalidOperationException>(() =>
            DiagnosticInsightAnalyzer.Analyze([recorded], [],
                sourceAt: index => index));
    }

    [Fact]
    public void SameLoggerAndEcuButDifferentBusesKeepIndependentFaultHistories()
    {
        var fault = new DecodedDtc(0x123456, "123456", "Sensor", 0x08, "confirmed", "");
        var first = Transaction("ECM", 0x19, 1, fault) with
        {
            FrameIndices = [0],
            Responses = [new IsoTpMessage(0, 0x7E8, false, 1, 1,
                [0], [0x59, 0x02], true, "")]
        };
        var second = Transaction("ECM", 0x19, 2, fault) with
        {
            FrameIndices = [1],
            Responses = [new IsoTpMessage(1, 0x7E8, false, 2, 2,
                [1], [0x59, 0x02], true, "")]
        };
        var history = DiagnosticInsightAnalyzer.Analyze([first, second], [],
            sourceAt: _ => 0).Faults;
        Assert.Equal(2, history.Count);
        Assert.Equal(new[] { 0, 1 },
            history.Select(row => row.SourceChannel).Order().ToArray());
    }


    [Fact]
    public void ACodeOnlyCatalogDtcMatchMustBeMarkedAsUnverifiedForAnotherEcu()
    {
        var trouble = new DecodedDtc(0x123456, "123456", "Fault", 0x08, "confirmed", "");
        var catalog = new RbgCanInsight.DiagData.DidCatalog(
            "unrelated", "BCM", "unrelated.cdd", [],
            [new RbgCanInsight.DiagData.DiagnosticTroubleCode(
                0x123456, "Fault", "Possible metadata",
                "Another ECU's description", "BCM", "unrelated.cdd")]);
        var row = Assert.Single(DiagnosticInsightAnalyzer.Analyze(
            [Transaction("ECM", 0x19, 1, trouble)], [catalog]).Faults);
        Assert.False(row.DefinitionEcuMatched);
        Assert.Contains("ECU not verified", row.DefinitionText);
        Assert.Equal("unrelated.cdd", row.DefinitionSourceFile);
        Assert.Equal(string.Empty, row.Name);
        Assert.Equal(string.Empty, row.Description);
        Assert.Empty(row.DefinitionAttributes);
    }

    [Fact]
    public void IdenticalDtcNumbersOnDifferentEcusRemainIndependent()
    {
        var trouble = new DecodedDtc(0x123456, "123456", "Sample fault",
            0x08, "confirmed", "");
        var transactions = new[]
        {
            Transaction("ECM", 0x19, 1, trouble),
            Transaction("BCM", 0x19, 2, trouble),
            Transaction("ECM", 0x14, 3)
        };

        var results = DiagnosticInsightAnalyzer.Analyze(transactions, []).Faults;

        Assert.Equal(2, results.Count);
        var engine = Assert.Single(results, fault => fault.Ecu == "ECM");
        var body = Assert.Single(results, fault => fault.Ecu == "BCM");
        Assert.True(engine.WasCleared);
        Assert.Equal((byte)0, engine.CurrentStatus);
        Assert.False(body.WasCleared);
        Assert.Equal((byte)0x08, body.CurrentStatus);
    }

    [Fact]
    public void ClearingOneDtcGroupDoesNotClearUnrelatedFaultsOnSameEcu()
    {
        var first = new DecodedDtc(0x123456, "123456", "Engine fault A",
            0x08, "confirmed", "");
        var second = new DecodedDtc(0xABCDEF, "ABCDEF", "Engine fault B",
            0x08, "confirmed", "");
        var faults = DiagnosticInsightAnalyzer.Analyze(
            [
                Transaction("ECM", 0x19, 1, first),
                Transaction("ECM", 0x19, 2, second),
                Transaction("ECM", 0x14, 3, clearGroup: 0x123456)
            ], []).Faults;
        Assert.True(Assert.Single(faults, fault => fault.Code == 0x123456).WasCleared);
        Assert.False(Assert.Single(faults, fault => fault.Code == 0xABCDEF).WasCleared);
    }

    [Fact]
    public void ClearWithoutRecordedRequestGroupIsNeverInterpretedAsClearAll()
    {
        var fault = new DecodedDtc(0x123456, "123456", "Fault", 0x08, "confirmed", "");
        var unprovenClear = Transaction("ECM", 0x14, 2) with { Request = null };
        var result = Assert.Single(DiagnosticInsightAnalyzer.Analyze(
            [Transaction("ECM", 0x19, 1, fault), unprovenClear], []).Faults);
        Assert.False(result.WasCleared);
    }

    [Fact]
    public void AReobservedFaultIsNotShownAsCurrentlyCleared()
    {
        var trouble = new DecodedDtc(0x123456, "123456", "Sample fault",
            0x08, "confirmed", "");
        var transactions = new[]
        {
            Transaction("ECM", 0x19, 1, trouble),
            Transaction("ECM", 0x14, 2),
            Transaction("ECM", 0x19, 3, trouble)
        };
        var fault = Assert.Single(DiagnosticInsightAnalyzer.Analyze(transactions, []).Faults);
        Assert.True(fault.WasCleared);
        Assert.Equal((byte)0x08, fault.CurrentStatus);
    }
}
