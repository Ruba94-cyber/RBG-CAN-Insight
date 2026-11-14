using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Adversarial evidence fixtures: two real logging files may share
/// channel numbers, CAN identifiers and overlapping wall-clock timestamps.
/// Neither ISO-TP bytes nor request/response/FC evidence may cross files.</summary>
public sealed class MultifileUdsIsolationTests
{
    private static CanFrame Frame(uint id, long time, params byte[] payload) =>
        new(0, id, payload, time);

    [Fact]
    public void AConsecutiveFrameFromAnotherLoggerCannotCompleteTheFirstLoggersPdu()
    {
        var frames = new[]
        {
            Frame(0x7E8, 0, 0x10, 0x0B, 0x62, 0xF1, 0x90, 0xA1, 0xA2, 0xA3),
            Frame(0x7E8, 1_000_000, 0x21, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8),
            Frame(0x7E8, 2_000_000, 0x21, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8)
        };
        var source = new[] { 0, 1, 0 };
        var pdus = IsoTpReassembler.Reassemble(frames, sourceAt: i => source[i]);
        var actual = Assert.Single(pdus);
        Assert.True(actual.IsComplete);
        Assert.Equal(new[] { 0, 2 }, actual.FrameIndices);
        Assert.Equal(new byte[] { 0x62, 0xF1, 0x90, 0xA1, 0xA2, 0xA3,
            0xA4, 0xA5, 0xA6, 0xA7, 0xA8 }, actual.Payload);
        Assert.DoesNotContain(1, actual.FrameIndices);
    }

    [Fact]
    public void IndependentFirstFramesWithIdenticalCanIdentityBothComplete()
    {
        var frames = new[]
        {
            Frame(0x7E8, 0, 0x10, 0x0B, 0x62, 0xF1, 0x90, 0xA1, 0xA2, 0xA3),
            Frame(0x7E8, 1_000_000, 0x10, 0x0B, 0x62, 0xF1, 0x87, 0xB1, 0xB2, 0xB3),
            Frame(0x7E8, 2_000_000, 0x21, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8),
            Frame(0x7E8, 3_000_000, 0x21, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8)
        };
        var source = new[] { 0, 1, 0, 1 };
        var pdus = IsoTpReassembler.Reassemble(frames, sourceAt: i => source[i]);
        Assert.Equal(2, pdus.Count);
        Assert.All(pdus, pdu => Assert.True(pdu.IsComplete));
        Assert.Contains(pdus, pdu => pdu.FrameIndices.SequenceEqual(new[] { 0, 2 }) &&
                                     pdu.Payload[2] == 0x90);
        Assert.Contains(pdus, pdu => pdu.FrameIndices.SequenceEqual(new[] { 1, 3 }) &&
                                     pdu.Payload[2] == 0x87);
    }

    [Fact]
    public void ForeignLoggerPositiveResponseNeverCompletesRequestFromOtherCapture()
    {
        var frames = new[]
        {
            Frame(0x7E0, 0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0x7E8, 1_000_000, 0x04, 0x62, 0xF1, 0x90, 0xB0),
            Frame(0x7E8, 2_000_000, 0x04, 0x62, 0xF1, 0x90, 0xA0)
        };
        var source = new[] { 0, 1, 0 };
        var result = UdsTransactionAnalyzer.Analyze(
            frames, null, null, CancellationToken.None, i => source[i]);
        var positive = Assert.Single(result.Where(t => t.Outcome == DiagnosticOutcome.Positive));
        Assert.Equal(new[] { 0, 2 }, positive.FrameIndices);
        Assert.Contains(result, t => t.Outcome == DiagnosticOutcome.OrphanResponse &&
                                     t.FrameIndices.SequenceEqual(new[] { 1 }));
        Assert.DoesNotContain(1, positive.FrameIndices);
    }

    [Fact]
    public void EqualRequestsAndEqualRepliesFromTwoLoggersRemainTwoTransactions()
    {
        var frames = new[]
        {
            Frame(0x7E0, 0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0x7E0, 1_000, 0x03, 0x22, 0xF1, 0x90),
            Frame(0x7E8, 2_000, 0x04, 0x62, 0xF1, 0x90, 0xA0),
            Frame(0x7E8, 3_000, 0x04, 0x62, 0xF1, 0x90, 0xB0)
        };
        var source = new[] { 0, 1, 0, 1 };
        var result = UdsTransactionAnalyzer.Analyze(
            frames, null, null, CancellationToken.None, i => source[i]);
        Assert.Equal(2, result.Count);
        Assert.All(result, t => Assert.Equal(DiagnosticOutcome.Positive, t.Outcome));
        Assert.Contains(result, t => t.FrameIndices.SequenceEqual(new[] { 0, 2 }));
        Assert.Contains(result, t => t.FrameIndices.SequenceEqual(new[] { 1, 3 }));
    }

    [Fact]
    public void FlowControlFromForeignLoggerNeverBecomesSourceEvidence()
    {
        var frames = new[]
        {
            Frame(0x7E0, 0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0x7E8, 1_000, 0x30, 0x00, 0x00),
            Frame(0x7E8, 2_000, 0x30, 0x00, 0x00),
            Frame(0x7E8, 3_000, 0x04, 0x62, 0xF1, 0x90, 0xA0)
        };
        var source = new[] { 0, 1, 0, 0 };
        var result = UdsTransactionAnalyzer.Analyze(
            frames, null, null, CancellationToken.None, i => source[i]);
        var transaction = Assert.Single(result);
        Assert.Equal(DiagnosticOutcome.Positive, transaction.Outcome);
        Assert.Equal(new[] { 0, 2, 3 }, transaction.FrameIndices);
        Assert.Contains("1 FC candidate", transaction.Quality);
        Assert.DoesNotContain(1, transaction.FrameIndices);
    }

    [Fact]
    public void BadPhysicalSourceIndexIsRejectedRatherThanProducingUntrustedEvidence()
    {
        var frames = new[] { Frame(0x7E8, 0, 0x04, 0x62, 0xF1, 0x90, 0xA0) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IsoTpReassembler.Reassemble(frames, sourceAt: _ => -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UdsTransactionAnalyzer.Analyze(frames, null, null,
                CancellationToken.None, _ => -1));
    }

    [Fact]
    public void LegacySingleCaptureCallRemainsSupportedWithoutSourceMapping()
    {
        var frames = new[]
        {
            Frame(0x7E0, 0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0x7E8, 1_000_000, 0x04, 0x62, 0xF1, 0x90, 0xA0)
        };
        var legacy = Assert.Single(UdsTransactionAnalyzer.Analyze(frames));
        var mapped = Assert.Single(UdsTransactionAnalyzer.Analyze(
            frames, null, null, CancellationToken.None, _ => 0));
        Assert.Equal(legacy.FrameIndices, mapped.FrameIndices);
        Assert.Equal(legacy.Outcome, mapped.Outcome);
    }
}
