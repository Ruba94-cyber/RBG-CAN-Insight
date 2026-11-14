using System.Threading;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class UdsTransactionAnalyzerTests
{
    /// <summary>Records every report synchronously, unlike <see cref="Progress{T}"/> which posts
    /// through a captured SynchronizationContext and would make assertions non-deterministic on a
    /// plain test thread with no message pump.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];
        public void Report(T value) => Reports.Add(value);
    }

    [Fact]
    public void IncludesFlowControlFrameWithinTransactionSpanViaIndex()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0], 0),
            Frame(0x7E8, [0x30, 0x00, 0x00, 0, 0, 0, 0, 0], 1_000_000),
            Frame(0x7E8, [0x05, 0x62, 0xF1, 0x90, 0x41, 0x42, 0, 0], 5_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Contains(1, transaction.FrameIndices);
        Assert.Equal(3, transaction.FrameIndices.Count);
        Assert.Contains("FC candidate", transaction.Quality);
    }

    [Fact]
    public void FlowControlEvidenceNeverIncludesUnrelatedIdsBusesOrErrorRecords()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0], 0),
            Frame(0x100, [0x30, 0, 0, 0, 0, 0, 0, 0], 1_000_000),
            new CanFrame(0, 0x7E8, [0x30, 0, 0, 0], 2_000_000,
                CanFrameFlags.Error),
            Frame(0x7E8, [0x3F, 0, 0, 0], 3_000_000),
            Frame(0x7E8, [0x30, 0, 0, 0], 4_000_000),
            new CanFrame(1, 0x7E8, [0x30, 0, 0], 4_500_000),
            Frame(0x7E8, [0x05, 0x62, 0xF1, 0x90, 0x41, 0x42, 0, 0], 5_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(new[] { 0, 4, 6 }, transaction.FrameIndices);
    }

    [Fact]
    public void CatalogEcuRequiresMatchingCanFrameFormatOnRequestAndResponse()
    {
        var source = new DidCatalog("source", "ExtendedECU", "synthetic.odx",
            Array.Empty<DidParameter>(), Connections:
            [
                new DiagnosticConnection("Extended", 0x650, 0x651, true,
                    "normal", "ExtendedECU", "synthetic.odx")
            ]);
        var standard = new[]
        {
            Frame(0x650, [0x03, 0x22, 0xF1, 0x90], 0),
            Frame(0x651, [0x04, 0x62, 0xF1, 0x90, 0x01], 1_000_000)
        };
        var extended = new[]
        {
            new CanFrame(0, 0x650, [0x03, 0x22, 0xF1, 0x90], 0,
                CanFrameFlags.Extended),
            new CanFrame(0, 0x651, [0x04, 0x62, 0xF1, 0x90, 0x01],
                1_000_000, CanFrameFlags.Extended)
        };

        var standardResult = Assert.Single(UdsTransactionAnalyzer.Analyze(standard, [source]));
        Assert.Equal(DiagnosticEvidence.Unknown, standardResult.Endpoint.Evidence);
        Assert.NotEqual("ExtendedECU", standardResult.Endpoint.Ecu);

        var extendedResult = Assert.Single(UdsTransactionAnalyzer.Analyze(extended, [source]));
        Assert.Equal(DiagnosticEvidence.Confirmed, extendedResult.Endpoint.Evidence);
        Assert.Equal("ExtendedECU", extendedResult.Endpoint.Ecu);
    }

    [Fact]
    public void ConflictingCatalogConnectionsCannotConfirmAnArbitrarilyChosenEcu()
    {
        var source = new DidCatalog("conflict", "Variants", "synthetic.odx",
            Array.Empty<DidParameter>(), Connections:
            [
                new DiagnosticConnection("VariantA", 0x650, 0x651, false,
                    "normal", "ECU_A", "synthetic.odx"),
                new DiagnosticConnection("VariantB", 0x650, 0x651, false,
                    "normal", "ECU_B", "synthetic.odx")
            ]);
        var frames = new[]
        {
            Frame(0x650, [0x03, 0x22, 0xF1, 0x90], 0),
            Frame(0x651, [0x04, 0x62, 0xF1, 0x90, 0x01], 1_000_000)
        };
        var result = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [source]));

        Assert.Equal(DiagnosticEvidence.Unknown, result.Endpoint.Evidence);
        Assert.NotEqual("ECU_A", result.Endpoint.Ecu);
        Assert.NotEqual("ECU_B", result.Endpoint.Ecu);
    }

    [Fact]
    public void DeclaredOemReplyIdentifierCannotBeReplacedBySidAndTimingGuess()
    {
        var catalog = new DidCatalog("ECM", "ECM", "synthetic.odx",
            [new DidParameter(0xF405, "Load", "Load", "", "",
                null, null, 1, 0, "A_UINT8", 8, "ECM", "synthetic.odx")],
            Connections:
            [
                new DiagnosticConnection("ECM route", 0x650, 0x651, false,
                    "normal", "ECM", "synthetic.odx")
            ]);
        var frames = new[]
        {
            Frame(0x650, [0x03, 0x22, 0xF4, 0x05], 0),
            // Same UDS service and DID, but wrong source CAN reply ID.
            Frame(0x652, [0x04, 0x62, 0xF4, 0x05, 0x10], 1_000_000),
            Frame(0x651, [0x04, 0x62, 0xF4, 0x05, 0x20], 2_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal("ECM", transaction.Endpoint.Ecu);
        Assert.Equal(DiagnosticEvidence.Confirmed, transaction.Endpoint.Evidence);
        Assert.Equal(new[] { 0, 2 }, transaction.FrameIndices);
        Assert.Equal(32d, Assert.Single(transaction.Decoded.Values).NumericValue);
    }

    [Fact]
    public void TwoCatalogRoutesSharingRequestIdResolveEcuByActualResponseId()
    {
        var catalog = new DidCatalog("ECUs", "Variants", "synthetic.odx",
        [
            new DidParameter(0xF405, "EcmLoad", "EcmLoad", "", "",
                null, null, 1, 0, "A_UINT8", 8, "ECM", "synthetic.odx"),
            new DidParameter(0xF405, "BcmLoad", "BcmLoad", "", "",
                null, null, 10, 0, "A_UINT8", 8, "BCM", "synthetic.odx")
        ], Connections:
        [
            new DiagnosticConnection("ECM", 0x650, 0x651, false,
                "normal", "ECM", "synthetic.odx"),
            new DiagnosticConnection("BCM", 0x650, 0x652, false,
                "normal", "BCM", "synthetic.odx")
        ]);
        var frames = new[]
        {
            Frame(0x650, [0x03, 0x22, 0xF4, 0x05], 0),
            Frame(0x652, [0x04, 0x62, 0xF4, 0x05, 0x20], 1_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        Assert.Equal(DiagnosticEvidence.Confirmed, transaction.Endpoint.Evidence);
        Assert.Equal("BCM", transaction.Endpoint.Ecu);
        Assert.Equal(320d, Assert.Single(transaction.Decoded.Values).NumericValue);
        Assert.Equal(new[] { 0, 1 }, transaction.FrameIndices);
    }

    [Fact]
    public void AConfirmedEcmRouteNeverBorrowsOtherEcusOnlyDidDefinition()
    {
        var catalog = new DidCatalog("ECM route with BCM-only definition",
            "ECM", "synthetic.odx",
            [new DidParameter(0xF405, "BcmOnly", "BcmOnly", "", "",
                null, null, 10, 0, "A_UINT8", 8, "BCM", "synthetic.odx")],
            Connections:
            [
                new DiagnosticConnection("ECM", 0x650, 0x651, false,
                    "normal", "ECM", "synthetic.odx")
            ]);
        var frames = new[]
        {
            Frame(0x650, [0x03, 0x22, 0xF4, 0x05], 0),
            Frame(0x651, [0x04, 0x62, 0xF4, 0x05, 0x20], 1_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        Assert.Equal(DiagnosticEvidence.Confirmed, transaction.Endpoint.Evidence);
        Assert.Equal("ECM", transaction.Endpoint.Ecu);
        var value = Assert.Single(transaction.Decoded.Values);
        Assert.Null(value.NumericValue);
        Assert.DoesNotContain("BcmOnly", value.Name, StringComparison.OrdinalIgnoreCase);
        Assert.False(value.HasVerifiedSource); // no ECU-compatible length declaration
    }

    [Fact]
    public void IndexedPendingRequestsMatchOnlyTheirOriginalBusAndService()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF4, 0x05], 0),
            new CanFrame(1, 0x7E0, [0x03, 0x22, 0xF4, 0x06], 1_000_000),
            Frame(0x7E0, [0x02, 0x10, 0x01], 2_000_000),
            new CanFrame(1, 0x7E8, [0x04, 0x62, 0xF4, 0x06, 0x22], 3_000_000),
            Frame(0x7E8, [0x04, 0x62, 0xF4, 0x05, 0x11], 4_000_000),
            Frame(0x7E8, [0x02, 0x50, 0x01], 5_000_000)
        };
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(3, transactions.Count);
        Assert.All(transactions, tx => Assert.Equal(DiagnosticOutcome.Positive, tx.Outcome));
        var channel1 = Assert.Single(transactions.Where(tx => tx.Request?.Channel == 1));
        Assert.Equal(0x22, channel1.Decoded.ServiceId);
        Assert.Equal(0xF406, Assert.Single(channel1.Decoded.Values).Did);
        Assert.Equal(new[] { 1, 3 }, channel1.FrameIndices);

        var session = Assert.Single(transactions.Where(tx => tx.Decoded.ServiceId == 0x10));
        Assert.Equal(new[] { 2, 5 }, session.FrameIndices);
        var channel0Did = Assert.Single(transactions.Where(tx =>
            tx.Decoded.ServiceId == 0x22 && tx.Request?.Channel == 0));
        Assert.Equal(0xF405, Assert.Single(channel0Did.Decoded.Values).Did);
        Assert.Equal(new[] { 0, 4 }, channel0Did.FrameIndices);
    }

    [Fact]
    public void NegativeNrcOnOneBusIsNotAmbiguousBecauseAnotherBusHasSameRequest()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF4, 0x05], 0),
            new CanFrame(1, 0x7E0, [0x03, 0x22, 0xF4, 0x05], 1_000_000),
            Frame(0x7E8, [0x03, 0x7F, 0x22, 0x31], 2_000_000)
        };
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(2, transactions.Count);
        var negative = Assert.Single(transactions.Where(tx =>
            tx.Outcome == DiagnosticOutcome.Negative));
        Assert.Equal(0, negative.Request!.Channel);
        Assert.Equal(new[] { 0, 2 }, negative.FrameIndices);
        Assert.Equal(DiagnosticOutcome.NoResponse,
            Assert.Single(transactions.Where(tx => tx.Request?.Channel == 1)).Outcome);
        Assert.DoesNotContain(transactions, tx =>
            tx.Outcome == DiagnosticOutcome.OrphanResponse);
    }

    [Fact]
    public void ReportsProgressReachingTotalForBothPhases()
    {
        var frames = new List<CanFrame>();
        for (var i = 0; i < 300; i++)
        {
            var timestamp = i * 10_000_000L;
            frames.Add(Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0], timestamp));
            frames.Add(Frame(0x7E8, [0x05, 0x62, 0xF1, 0x90, 0x41, 0x42, 0, 0], timestamp + 1_000_000));
        }

        var progress = new SyncProgress<UdsAnalysisProgress>();
        var transactions = UdsTransactionAnalyzer.Analyze(frames.ToArray(), null, progress, CancellationToken.None);

        Assert.Equal(300, transactions.Count);
        Assert.NotEmpty(progress.Reports);
        Assert.All(progress.Reports, report => Assert.True(report.Completed <= report.Total));
        Assert.Contains(progress.Reports, report =>
            report.Phase == "Reassembling ISO-TP messages" &&
            report.Completed == report.Total);
        Assert.Contains(progress.Reports, report => report.Phase == "Pairing requests and responses" && report.Completed == report.Total);
        Assert.Contains(progress.Reports, report => report.Phase == "Building transactions" && report.Completed == report.Total);
    }

    private sealed class CancelDuringReassembly(CancellationTokenSource cancellation)
        : IProgress<UdsAnalysisProgress>
    {
        public int ReassemblySamples { get; private set; }

        public void Report(UdsAnalysisProgress sample)
        {
            if (sample.Phase != "Reassembling ISO-TP messages")
                return;
            ReassemblySamples++;
            if (sample.Completed >= 4097)
                cancellation.Cancel();
        }
    }

    [Fact]
    public void CancelActuallyInterruptsIsoTpReassemblyBeforePairingBegins()
    {
        var frames = Enumerable.Range(0, 12_000).Select(index =>
            new CanFrame(0, 0x100,
                [0xF0, 1, 2, 3, 4, 5, 6, 7],
                index * 1_000L)).ToArray();
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelDuringReassembly(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            UdsTransactionAnalyzer.Analyze(frames, catalogs: null,
                progress, cancellation.Token));
        Assert.True(progress.ReassemblySamples >= 2);
    }

    [Fact]
    public void HonorsCancellationOnALargeCapture()
    {
        var frames = new List<CanFrame>();
        for (var i = 0; i < 300; i++)
        {
            var timestamp = i * 10_000_000L;
            frames.Add(Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0], timestamp));
            frames.Add(Frame(0x7E8, [0x05, 0x62, 0xF1, 0x90, 0x41, 0x42, 0, 0], timestamp + 1_000_000));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            UdsTransactionAnalyzer.Analyze(frames.ToArray(), null, progress: null, cts.Token));
    }

    [Fact]
    public void PairsPhysicalRequestAndResponse()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0], 0),
            Frame(0x7E8, [0x05, 0x62, 0xF1, 0x90, 0x41, 0x42, 0, 0], 5_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(0x22, transaction.Decoded.ServiceId);
        Assert.Contains("F190", transaction.Decoded.ObjectText);
        Assert.Equal(5, transaction.ResponseTimeMilliseconds);
        Assert.Equal(DiagnosticDirection.TesterToEcu, transaction.Endpoint.Direction);
    }

    [Fact]
    public void ReassemblesCanFdLengthAndMultiFrameResponse()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x00, 0x03, 0x22, 0xF1, 0x90, 0, 0, 0], 0),
            Frame(0x7E8, [0x10, 0x14, 0x62, 0xF1, 0x90, 0x31, 0x32, 0x33], 1_000_000),
            Frame(0x7E8, [0x21, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x41], 2_000_000),
            Frame(0x7E8, [0x22, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48], 3_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(3, transaction.Responses[0].FrameIndices.Count);
        Assert.Equal(20, transaction.Responses[0].Payload.Length);
        Assert.Contains("123456789ABCDEFGH", System.Text.Encoding.ASCII.GetString(transaction.Responses[0].Payload.ToArray()));
        Assert.Contains("DID 0xF190", transaction.Decoded.ResponseSummary);
    }

    [Fact]
    public void KeepsResponsePendingInsideFinalTransaction()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x02, 0x03, 0, 0, 0], 0),
            Frame(0x7E8, [0x03, 0x7F, 0x31, 0x78, 0, 0, 0, 0], 10_000_000),
            Frame(0x7E8, [0x04, 0x71, 0x01, 0x02, 0x03, 0, 0, 0], 20_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(2, transaction.Responses.Count);
        Assert.Contains("Routine 0x0203", transaction.Decoded.ObjectText);
    }

    [Fact]
    public void DecodesDtcAndStatusBits()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x19, 0x02, 0xFF, 0, 0, 0, 0], 0),
            Frame(0x7E8, [0x07, 0x59, 0x02, 0xFF, 0x12, 0x34, 0x56, 0x0D], 2_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        var dtc = Assert.Single(transaction.Decoded.Dtcs);

        Assert.Equal(0x123456, dtc.Code);
        Assert.Contains("confirmed", dtc.StatusText);
        Assert.Contains("pending", dtc.StatusText);
    }

    [Fact]
    public void OemIdentifiersArePairedButMarkedUnknown()
    {
        var frames = new[]
        {
            Frame(0x651, [0x02, 0x10, 0x03, 0, 0, 0, 0, 0], 0),
            Frame(0x652, [0x02, 0x50, 0x03, 0, 0, 0, 0, 0], 1_000_000)
        };

        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));

        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(DiagnosticEvidence.Unknown, transaction.Endpoint.Evidence);
        Assert.Contains("OEM", transaction.Endpoint.Addressing);
    }


    [Fact]
    public void AmbiguousNegativeResponseCannotBeAssignedToAnArbitraryOpenDid()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90], 0),
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x87], 1_000_000),
            // NRC has SID but no DID, so it does not identify which read failed.
            Frame(0x7E8, [0x03, 0x7F, 0x22, 0x31], 2_000_000)
        };

        var transactions = UdsTransactionAnalyzer.Analyze(frames);

        Assert.Equal(3, transactions.Count);
        Assert.Equal(2, transactions.Count(item => item.Outcome == DiagnosticOutcome.NoResponse));
        var orphan = Assert.Single(transactions.Where(
            item => item.Outcome == DiagnosticOutcome.OrphanResponse));
        Assert.Null(orphan.Request);
        Assert.Contains("Ambiguous", orphan.Quality);
        Assert.Contains("multiple open requests", orphan.Quality);
        Assert.Equal(new[] { 2 }, orphan.FrameIndices);
        Assert.All(transactions.Where(item => item.Request is not null), item =>
            Assert.Empty(item.Responses));
    }

    [Fact]
    public void SingleOutstandingRequestStillReceivesItsNegativeResponse()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90], 0),
            Frame(0x7E8, [0x03, 0x7F, 0x22, 0x31], 2_000_000)
        };
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        Assert.Equal(DiagnosticOutcome.Negative, transaction.Outcome);
        Assert.Equal((byte?)0x31, transaction.Decoded.NegativeResponseCode);
        Assert.Equal(new[] { 0, 1 }, transaction.FrameIndices);
    }

    [Fact]
    public void PositiveResponseCannotBeAssignedToNewestOfTwoIdenticalOpenRequests()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF4, 0x05], 0),
            Frame(0x7E0, [0x03, 0x22, 0xF4, 0x05], 1_000_000),
            Frame(0x7E8, [0x04, 0x62, 0xF4, 0x05, 0x20], 2_000_000)
        };
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(3, transactions.Count);
        Assert.Equal(2, transactions.Count(tx =>
            tx.Outcome == DiagnosticOutcome.NoResponse));
        Assert.DoesNotContain(transactions, tx =>
            tx.Outcome == DiagnosticOutcome.Positive && tx.Request is not null);
        var orphan = Assert.Single(transactions.Where(tx =>
            tx.Outcome == DiagnosticOutcome.OrphanResponse));
        Assert.Null(orphan.Request);
        Assert.Equal(new[] { 2 }, orphan.FrameIndices);
        Assert.Contains("Ambiguous ·", orphan.Quality);
        Assert.Contains("Positive UDS response cannot be uniquely paired", orphan.Quality);
    }

    [Fact]
    public void SharedFirstDidInTwoDifferentMultiDidRequestsIsNotAUniqueDiscriminator()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x05, 0x22, 0xF4, 0x05, 0xF4, 0x06], 0),
            Frame(0x7E0, [0x05, 0x22, 0xF4, 0x05, 0xF4, 0x07], 1_000_000),
            Frame(0x7E8, [0x04, 0x62, 0xF4, 0x05, 0x20], 2_000_000)
        };
        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        var orphan = Assert.Single(transactions.Where(tx =>
            tx.Outcome == DiagnosticOutcome.OrphanResponse));
        Assert.Contains("Ambiguous", orphan.Quality);
        Assert.DoesNotContain(transactions, tx =>
            tx.Outcome == DiagnosticOutcome.Positive && tx.Request is not null);
    }

    [Fact]
    public void OverlappingReadDidRequestsAreNotPairedByServiceAlone()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0], 0),
            Frame(0x7E0, [0x03, 0x22, 0xF1, 0x87, 0, 0, 0, 0], 1_000_000),
            // Response to the older request is recorded first.
            Frame(0x7E8, [0x04, 0x62, 0xF1, 0x90, 0x31, 0, 0, 0], 2_000_000),
            Frame(0x7E8, [0x04, 0x62, 0xF1, 0x87, 0x32, 0, 0, 0], 3_000_000)
        };

        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(2, transactions.Count);
        foreach (var transaction in transactions)
        {
            Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
            Assert.NotNull(transaction.Request);
            Assert.Single(transaction.Responses);
            Assert.Equal(transaction.Request!.Payload[1], transaction.Responses[0].Payload[1]);
            Assert.Equal(transaction.Request!.Payload[2], transaction.Responses[0].Payload[2]);
        }
    }

    [Fact]
    public void RoutineResponsesRespectRoutineIdentifier()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x12, 0x34, 0, 0, 0], 0),
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x12, 0x35, 0, 0, 0], 1_000_000),
            Frame(0x7E8, [0x04, 0x71, 0x01, 0x12, 0x34, 0, 0, 0], 2_000_000),
            Frame(0x7E8, [0x04, 0x71, 0x01, 0x12, 0x35, 0, 0, 0], 3_000_000)
        };

        var transactions = UdsTransactionAnalyzer.Analyze(frames);
        Assert.Equal(2, transactions.Count);
        Assert.All(transactions, transaction =>
        {
            Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
            Assert.Equal(transaction.Request!.Payload[3], transaction.Responses[0].Payload[3]);
        });
    }

    private static CanFrame Frame(uint id, byte[] data, long timestamp) =>
        new(0, id, data, timestamp, CanFrameFlags.None);
}
