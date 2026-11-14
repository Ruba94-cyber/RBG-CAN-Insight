using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Regressions for defects found by the 2026-11-11 ISO-TP / UDS audit. Each expected
/// interpretation follows the ISO 14229-1 message layout quoted next to the payload.</summary>
public sealed class UdsAuditRegressionTests
{
    private const long Ms = 1_000_000L;
    private const long Second = 1_000_000_000L;

    private static CanFrame Frame(uint id, byte[] payload, long timestampNs) => new(0, id, payload, timestampNs);

    private static CanFrame Extended(uint id, byte[] payload, long timestampNs) =>
        new(0, id, payload, timestampNs, CanFrameFlags.Extended);

    [Fact]
    public void FirstTestFailedDtcSkipsTheStatusAvailabilityMask()
    {
        // 59 0B | availabilityMask 7F | DTC 12 34 56 | status 2F
        var frames = new[]
        {
            Frame(0x7E0, [0x02, 0x19, 0x0B], 0),
            Frame(0x7E8, [0x07, 0x59, 0x0B, 0x7F, 0x12, 0x34, 0x56, 0x2F], 10 * Ms)
        };

        var dtc = Assert.Single(Assert.Single(UdsTransactionAnalyzer.Analyze(frames)).Decoded.Dtcs);

        Assert.Equal(0x123456, dtc.Code);
        Assert.Equal((byte)0x2F, dtc.Status);
    }

    [Fact]
    public void SeverityInformationRecordIsSeverityFunctionalUnitDtcStatus()
    {
        // 59 09 | availabilityMask FF | severity 02 | functionalUnit 01 | DTC 12 34 56 | status 2F
        var frames = new[]
        {
            Frame(0x7E0, [0x05, 0x19, 0x09, 0x12, 0x34, 0x56], 0),
            Frame(0x7E8, [0x10, 0x09, 0x59, 0x09, 0xFF, 0x02, 0x01, 0x12], 10 * Ms),
            Frame(0x7E0, [0x30, 0x00, 0x00], 11 * Ms),
            Frame(0x7E8, [0x21, 0x34, 0x56, 0x2F], 12 * Ms)
        };

        var dtc = Assert.Single(Assert.Single(UdsTransactionAnalyzer.Analyze(frames)).Decoded.Dtcs);

        Assert.Equal(0x123456, dtc.Code);
        Assert.Equal((byte)0x2F, dtc.Status);
        Assert.Contains("severity 0x02", dtc.ExtraData);
        Assert.Contains("functional unit 0x01", dtc.ExtraData);
    }

    [Fact]
    public void ReportTypeWithoutAKnownRecordLayoutStaysRaw()
    {
        // 0x1A reportSupportedDTCExtDataRecord carries an extra record-number byte before the
        // DTC list; guessing 4-byte records at offset 2 would invent DTC 0xFF0112.
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x19, 0x1A, 0x01], 0),
            Frame(0x7E8, [0x07, 0x59, 0x1A, 0xFF, 0x01, 0x12, 0x34, 0x56], 10 * Ms)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Empty(transaction.Decoded.Dtcs);
        Assert.Contains("FF 01 12 34 56", transaction.Decoded.ResponseSummary);
    }

    [Fact]
    public void FunctionalRequestGetsOneTransactionPerRespondingEcu()
    {
        var frames = new[]
        {
            Frame(0x7DF, [0x02, 0x3E, 0x00], 0),
            Frame(0x7E8, [0x02, 0x7E, 0x00], 5 * Ms),
            Frame(0x7E9, [0x02, 0x7E, 0x00], 6 * Ms)
        };

        var transactions = UdsTransactionAnalyzer.Analyze(frames);

        Assert.Equal(2, transactions.Count);
        Assert.All(transactions, t => Assert.Equal(DiagnosticOutcome.Positive, t.Outcome));
        Assert.All(transactions, t => Assert.NotNull(t.Request));
        Assert.Equal([0x7E8u, 0x7E9u],
            transactions.Select(t => Assert.Single(t.Responses).Identifier).OrderBy(id => id));
        Assert.Equal(2, transactions.Select(t => t.Endpoint.Ecu).Distinct().Count());
    }

    [Fact]
    public void FunctionalDtcReadKeepsEachEcuFaultsApart()
    {
        // 59 02 | mask FF | DTC + status
        var frames = new[]
        {
            Frame(0x7DF, [0x03, 0x19, 0x02, 0xFF], 0),
            Frame(0x7E8, [0x07, 0x59, 0x02, 0xFF, 0x11, 0x11, 0x11, 0x08], 5 * Ms),
            Frame(0x7E9, [0x07, 0x59, 0x02, 0xFF, 0x22, 0x22, 0x22, 0x09], 6 * Ms)
        };

        var transactions = UdsTransactionAnalyzer.Analyze(frames)
            .OrderBy(t => t.Responses[0].Identifier).ToArray();

        Assert.Equal(2, transactions.Length);
        Assert.Equal(0x111111, Assert.Single(transactions[0].Decoded.Dtcs).Code);
        Assert.Equal(0x222222, Assert.Single(transactions[1].Decoded.Dtcs).Code);
        Assert.NotEqual(transactions[0].Endpoint.Ecu, transactions[1].Endpoint.Ecu);
    }

    [Fact]
    public void FunctionalRequestWithoutAnyAnswerIsStillReported()
    {
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze([Frame(0x7DF, [0x02, 0x10, 0x03], 0)]));

        Assert.Equal(DiagnosticOutcome.NoResponse, transaction.Outcome);
    }

    [Fact]
    public void ExtendedFunctionalRequestPairsWithPhysicalResponses()
    {
        // 0x18DB33F1: functional, target 0x33, tester 0xF1. ECUs 0x10 and 0x17 answer on 0x18DAF1xx.
        var frames = new[]
        {
            Extended(0x18DB33F1, [0x02, 0x3E, 0x00], 0),
            Extended(0x18DAF110, [0x02, 0x7E, 0x00], 5 * Ms),
            Extended(0x18DAF117, [0x02, 0x7E, 0x00], 6 * Ms)
        };

        var transactions = UdsTransactionAnalyzer.Analyze(frames);

        Assert.Equal(2, transactions.Count);
        Assert.All(transactions, t => Assert.Equal(DiagnosticOutcome.Positive, t.Outcome));
    }

    [Fact]
    public void SuppressedRequestDoesNotStealOrBlockALaterPositiveResponse()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x02, 0x3E, 0x80], 0),             // suppressPosRspMsgIndicationBit: no answer expected
            Frame(0x7E0, [0x02, 0x3E, 0x00], 2 * Second),
            Frame(0x7E8, [0x02, 0x7E, 0x00], 2 * Second + 10 * Ms)
        };

        var transactions = UdsTransactionAnalyzer.Analyze(frames);

        Assert.Equal(2, transactions.Count);
        var suppressed = Assert.Single(transactions, t => t.Outcome == DiagnosticOutcome.SuppressedResponse);
        Assert.Equal(0x80, suppressed.Request!.Payload[1]);
        var answered = Assert.Single(transactions, t => t.Outcome == DiagnosticOutcome.Positive);
        Assert.Equal(0x00, answered.Request!.Payload[1]);
        Assert.Equal(2 * Second, answered.Request.StartTimestampNanoseconds);
    }

    [Fact]
    public void NonCompliantPositiveAnswerToASuppressedRequestIsStillPaired()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x02, 0x10, 0x83], 0),
            Frame(0x7E8, [0x06, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4], 10 * Ms)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
    }

    [Fact]
    public void RequestTransferExitWithoutParametersIsATransaction()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x01, 0x37], 0),
            Frame(0x7E8, [0x01, 0x77], 10 * Ms)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Equal((byte)0x37, transaction.Decoded.ServiceId);
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
    }

    [Fact]
    public void LoneServiceByteOnAnOrdinaryIdentifierIsNotTreatedAsDiagnostics()
    {
        var frames = new[]
        {
            Frame(0x123, [0x01, 0x37, 0, 0, 0, 0, 0, 0], 0),
            Frame(0x124, [0x01, 0x77, 0, 0, 0, 0, 0, 0], 10 * Ms)
        };

        Assert.Empty(UdsTransactionAnalyzer.Analyze(frames));
    }

    [Fact]
    public void FinalResponseAfterALongResponsePendingChainIsPaired()
    {
        var frames = new List<CanFrame> { Frame(0x7E0, [0x04, 0x31, 0x01, 0xFF, 0x00], 0) };
        for (var seconds = 5; seconds <= 40; seconds += 5)
            frames.Add(Frame(0x7E8, [0x03, 0x7F, 0x31, 0x78], seconds * Second));
        frames.Add(Frame(0x7E8, [0x04, 0x71, 0x01, 0xFF, 0x00], 45 * Second));

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(9, transaction.Responses.Count);
    }

    [Fact]
    public void ResponseLongAfterTheRequestWithoutPendingIsNotPaired()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x04, 0x31, 0x01, 0xFF, 0x00], 0),
            Frame(0x7E8, [0x04, 0x71, 0x01, 0xFF, 0x00], 45 * Second)
        };

        var transactions = UdsTransactionAnalyzer.Analyze(frames);

        Assert.Contains(transactions, t => t.Outcome == DiagnosticOutcome.NoResponse);
        Assert.DoesNotContain(transactions, t => t.Outcome == DiagnosticOutcome.Positive);
    }

    [Fact]
    public void TransferDataShowsTheFullBlockSequenceCounter()
    {
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(
            [Frame(0x7E0, [0x04, 0x36, 0x85, 0xAA, 0xBB], 0)]));

        Assert.Contains("block 0x85", transaction.Decoded.RequestSummary);
        Assert.DoesNotContain("sub 0x", transaction.Decoded.RequestSummary);
    }

    [Fact]
    public void RequestDownloadDecodesAddressAndSizeFromTheFormatIdentifier()
    {
        // 34 | dataFormat 00 | ALFID 44 (4 size bytes, 4 address bytes) | address 00080000 | size 00001000
        var frames = new[]
        {
            Frame(0x7E0, [0x10, 0x0B, 0x34, 0x00, 0x44, 0x00, 0x08, 0x00], 0),
            Frame(0x7E8, [0x30, 0x00, 0x00], 1 * Ms),
            Frame(0x7E0, [0x21, 0x00, 0x00, 0x00, 0x10, 0x00], 2 * Ms)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Contains("address 0x00080000", transaction.Decoded.RequestSummary);
        Assert.Contains("size 4096 byte(s)", transaction.Decoded.RequestSummary);
    }

    [Fact]
    public void RapidPowerShutdownIsNotAnEcuReset()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x02, 0x11, 0x04], 0),
            Frame(0x7E8, [0x03, 0x51, 0x04, 0x0A], 10 * Ms),
            Frame(0x7E0, [0x02, 0x11, 0x01], 1 * Second),
            Frame(0x7E8, [0x02, 0x51, 0x01], 1 * Second + 10 * Ms)
        };

        var changes = DiagnosticSessionStateTracker.Reconstruct(UdsTransactionAnalyzer.Analyze(frames)).Changes;

        var reset = Assert.Single(changes, change => change.Kind == DiagnosticStateChangeKind.EcuReset);
        Assert.Equal(0x01, reset.Value);
    }
}
